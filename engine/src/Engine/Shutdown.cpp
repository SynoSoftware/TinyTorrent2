#include "Engine/State.h"

namespace tt
{
namespace
{
// Bound pending priority edits, torrent pauses and the final transfer sample
// so a lost acknowledgement cannot leave Exit waiting indefinitely.
constexpr auto phaseTimeout = std::chrono::seconds(30);
}

void Engine::State::Shutdown(std::function<void(std::optional<std::string> failure)> report)
{
    if (shutdown)
    {
        return;
    }
    if (connectionTest)
        ReleaseConnectionTest(connectionTest->connectionId, ConnectionPhase::Cancelled);
    StartFinalSave();
    FailReading(Failure(ErrorCode::ShuttingDown));
    Discard([](Preview const&) { return true; });
    log.Write("shutdown", "", "requested");
    shutdown = std::move(report);
}

// Starts the shutdown phases, also again after a failed final save. A new
// attempt keeps `pausing`, so it waits again for torrents that never confirmed
// their pause instead of saving while their disk work may still run.
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

// Records that the current final save failed; it does nothing before
// shutdown. A failure without a cause keeps a cause already recorded, so the
// toast still names the file and Windows' reason.
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

// Shutdown waits for queued changes and additions, pauses every torrent,
// waits for outstanding checkpoints, and then saves every torrent a
// final time.
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
    if (shutdownPhase == ShutdownPhase::Retrying)
    {
        if (std::chrono::steady_clock::now() - *phaseStarted < checkpointRetry)
        {
            return;
        }
        StartFinalSave();
    }
    if (!changes.IsIdle() || !additions.empty())
    {
        return;
    }
    if (!payload.IsIdle())
        return;
    if (FilesBusy())
    {
        if (rename)
        {
            if (!phaseStarted)
                phaseStarted = std::chrono::steady_clock::now();
            if (std::chrono::steady_clock::now() - *phaseStarted >= phaseTimeout)
            {
                FailFinalSave();
                log.Write("shutdown", "", "rename_pending");
                RetryFinalSave();
                return;
            }
        }
        return;
    }
    if (shutdownPhase == ShutdownPhase::Draining)
    {
        if (!phaseStarted)
        {
            phaseStarted = std::chrono::steady_clock::now();
        }
        bool pending = false;
        for (auto& [id, torrent] : torrents)
        {
            QueryPriorities(torrent);
            if (!torrent.priorityCompletion)
            {
                continue;
            }
            if (std::chrono::steady_clock::now() - *phaseStarted < phaseTimeout)
            {
                pending = true;
                continue;
            }
            std::exchange(torrent.priorityCompletion, nullptr)({ErrorCode::RecoveryRequired});
            FailFinalSave();
            log.Write("edit", id, "recovery_required");
        }
        if (pending)
        {
            return;
        }
        shutdownPhase = ShutdownPhase::Pausing;
        phaseStarted = std::chrono::steady_clock::now();
        if (!session->is_paused())
        {
            for (auto const& [id, torrent] : torrents)
            {
                if (!torrent.restore && !bool(torrent.handle.flags() & lt::torrent_flags::paused))
                {
                    pausing.push_back(torrent.handle);
                }
            }
        }
        session->pause();
    }
    if (shutdownPhase == ShutdownPhase::Pausing)
    {
        if (!pausing.empty() && std::chrono::steady_clock::now() - *phaseStarted < phaseTimeout)
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
        // The torrents left in `pausing` did not confirm their pause in time.
        if (!pausing.empty())
        {
            FailFinalSave();
            for (auto const& handle : pausing)
            {
                log.Write("shutdown", handles.at(handle)->torrentId, "pause_timeout");
            }
        }
        shutdownPhase = ShutdownPhase::Sampling;
        phaseStarted = std::chrono::steady_clock::now();
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
        if (std::chrono::steady_clock::now() - *phaseStarted < phaseTimeout)
            return;
        FailFinalSave();
        log.Write("shutdown", "", "recovery_required");
        shutdownPhase = ShutdownPhase::Saving;
    }
    if (!saveFailure)
    {
        CheckpointUnsaved();
    }
    bool pending = false;
    for (auto const& [id, torrent] : torrents)
    {
        bool checkpointing = torrent.checkpointPhase != CheckpointPhase::Idle;
        pending |= checkpointing || (torrent.unsaved && !saveFailure);
    }
    // A payload or move job may still use the session, so it ends first.
    if (pending || !store.IsIdle() || !payload.IsIdle() || !copies.IsIdle() || !log.IsFlushed())
    {
        return;
    }
    if (saveFailure)
    {
        RetryFinalSave();
        return;
    }
    // Reporting first lets the desktop leave the screen before the teardown
    // waits on trackers.
    Finish();
    session.reset();
}

// Reports the failed attempt and starts the final save again after
// checkpointRetry, so Exit ends once a save succeeds.
void Engine::State::RetryFinalSave()
{
    shutdown(saveFailure);
    shutdownPhase = ShutdownPhase::Retrying;
    phaseStarted = std::chrono::steady_clock::now();
}

void Engine::State::Finish()
{
    auto report = std::exchange(shutdown, nullptr);
    report(std::nullopt);
}
}
