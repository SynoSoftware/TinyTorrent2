#include "Engine/State.h"
#include <algorithm>

namespace tt
{
namespace
{
// Bounds the wait for the moves to stop, storage reloads, additions, pending
// priority edits, torrent pauses and the final transfer sample, so that a
// lost acknowledgement or a drive that stopped responding cannot keep Exit
// from saving well within the 10 seconds after which the desktop ends the
// process.
constexpr auto phaseTimeout = std::chrono::seconds(2);
// The jobs that still run after the final save end in milliseconds unless
// their drive stopped responding, and then they may never return.
constexpr auto endingTimeout = std::chrono::seconds(1);
}

void Engine::State::Shutdown(std::function<void(ShutdownEnd end)> report)
{
    // Exit runs once, also when Windows ends the session after Exit ended
    // and the session is gone.
    if (IsShuttingDown())
    {
        return;
    }
    if (connectionTest)
        ReleaseConnectionTest(connectionTest->connectionId, ConnectionPhase::Cancelled);
    bool ready = startup == Startup::Ready;
    StartFinalSave();
    CancelFileWork();
    // An addition's disk work can wait on a share that does not answer: its
    // names on the drive's worker, and its new folder or its renames on
    // libtorrent's disk threads. So the addition joins the list without its
    // names and stays paused, and the next start chooses them.
    for (auto& [id, addition] : additions)
    {
        // One that waits for its folder's lookup joins the list too, with no
        // disk work through its folder: without its preview it is added here,
        // and with it it is saved below, as one that moves is.
        if (addition.phase == AdditionPhase::Locating && !addition.handle.is_valid())
        {
            addition.params.root_key = RootOf(addition.facts.savePath);
            addition.phase = AdditionPhase::Adding;
        }
        if (addition.phase == AdditionPhase::Adding)
        {
            if (!addition.params.userdata.get<Addition>())
            {
                addition.params.userdata = &addition;
                session->async_add_torrent(addition.params);
            }
        }
        else if (addition.phase != AdditionPhase::Saving)
        {
            addition.named = false;
            SaveAddition(id, addition.handle);
        }
    }
    FailReading(Failure(ErrorCode::ShuttingDown));
    Discard([](Preview const&) { return true; });
    log.Write("shutdown", "", "requested");
    shutdown = std::move(report);
    // The tasks are already saved. This save records that Exit cut them off,
    // and the marks being written, before anything waits.
    if (ready)
    {
        SaveDocument();
    }
}

void Engine::State::SaveDocument(std::function<void()> saved)
{
    if (!changes.Queue([this, saved = std::move(saved)]
    {
        auto document = Saved();
        changes.Commit([document = std::move(document)] { return document.ToJson(); },
            [this, saved](Outcome outcome)
        {
            if (outcome.error)
                FailFinalSave(outcome.detail);
            else if (saved)
                saved();
        });
    }))
    {
        FailFinalSave();
    }
}

void Engine::State::CancelFileWork()
{
    fileWork.Each([](FileWork::Job const& job)
    {
        auto none = MoveCancel::None;
        if (job.progress)
            job.progress->cancel.compare_exchange_strong(none, MoveCancel::Keep);
    });
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

// Shutdown waits for queued changes, gives the moves, storage reloads and
// additions a moment to stop, saves the proofs of the copies, pauses every
// torrent and saves every torrent a final time. Then it gives the work that
// still runs a moment, and ends without the work that runs on, which the next
// start resumes or restarts.
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
    // A save that fails now would fail again, so Exit ends as after a crash,
    // once the log holds the failure. The tasks and markers saved before it
    // still hold the work.
    if (saveFailure && shutdownPhase != ShutdownPhase::Failing)
    {
        log.Write("shutdown", "", "save_failed");
        shutdownPhase = ShutdownPhase::Failing;
    }
    if (shutdownPhase == ShutdownPhase::Failing)
    {
        if (store.IsIdle() && log.IsFlushed())
            std::exchange(shutdown, nullptr)(ShutdownEnd::Abrupt);
        return;
    }
    if (!changes.IsIdle())
    {
        return;
    }
    // File work on its own worker has stopped once the worker is idle, or
    // once a move's copy waits, parked, for the process to end.
    auto working = [this]
    {
        bool runs = false;
        fileWork.Each([&runs](FileWork::Job const& job)
        {
            runs = runs || (job.worker && !job.worker->IsIdle() && !(job.progress && job.progress->parked));
        });
        return runs;
    };
    auto now = std::chrono::steady_clock::now();
    if (!phaseStarted)
    {
        phaseStarted = now;
    }
    auto waited = now - *phaseStarted;
    bool expired = waited >= phaseTimeout;
    if (shutdownPhase == ShutdownPhase::Draining)
    {
        if (!expired && (!additions.empty() || working()))
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
        // drive that stopped responding may never do. Storage recovery and a
        // move hold their torrent paused.
        if (!session->is_paused())
        {
            for (auto const& [id, torrent] : torrents)
            {
                if (!torrent.restore && !torrent.control.Recovers() && !torrent.pathWork &&
                    !bool(torrent.handle.flags() & lt::torrent_flags::paused) &&
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
        // The second save of Exit, now that no completion can start a mark,
        // records the files that finished since the first.
        if (startup == Startup::Ready)
            SaveDocument([this] { log.Write("shutdown", "", "saved"); });
        else
            log.Write("shutdown", "", "saved");
        shutdownPhase = ShutdownPhase::Ending;
        phaseStarted = now;
        waited = {};
    }
    if (shutdownPhase == ShutdownPhase::Ending)
    {
        // A parked copy waits for the process to end, so nothing waits for it.
        fileWork.Each([](FileWork::Job const& job)
        {
            if (job.worker && job.progress && job.progress->parked)
                job.worker->Detach();
        });
        if (!store.IsIdle() || !log.IsFlushed())
        {
            return;
        }
        // A job that still runs can use the session, which the teardown ends
        // under it, and the engine's end joins its worker. libtorrent's
        // teardown also waits for a stalled check's reads. The file work
        // stops between files, so Exit gives it a moment, and otherwise ends
        // without the teardown: the saved tasks and markers hold that work.
        bool runs = working() || !sources.IsIdle() ||
            std::any_of(volumes.begin(), volumes.end(), [](auto const& entry) { return !entry.second->IsIdle(); }) ||
            std::any_of(rootProbes.begin(), rootProbes.end(),
                [](auto const& entry) { return !entry.second.worker->IsIdle(); });
        if (runs && waited < endingTimeout)
        {
            return;
        }
        if (!runs && StalledChecks().empty())
        {
            // Reporting first lets the desktop leave the screen before the
            // teardown waits on trackers.
            Finish();
            session.reset();
            return;
        }
        log.Write("shutdown", "", "teardown_skipped");
        shutdownPhase = ShutdownPhase::Cutting;
    }
    if (shutdownPhase == ShutdownPhase::Cutting && store.IsIdle() && log.IsFlushed())
    {
        std::exchange(shutdown, nullptr)(ShutdownEnd::Cut);
    }
}

void Engine::State::Finish()
{
    auto report = std::exchange(shutdown, nullptr);
    report(ShutdownEnd::Saved);
}
}
