#include "Engine/State.h"

namespace tt
{
namespace
{
// Bound pending priority edits, torrent pauses and the final transfer sample
// so a lost acknowledgement cannot leave Exit waiting indefinitely.
constexpr auto phaseTimeout = std::chrono::seconds(30);
}

void Engine::State::Shutdown(std::function<void(std::optional<std::string> failure)> completion)
{
    if (shutdown)
    {
        return;
    }
    // Exit after a failed final save starts its phases again.
    if (IsShuttingDown() && session)
    {
        saveFailure.reset();
        pausing.clear();
    }
    for (auto& [id, torrent] : torrents)
    {
        if (torrent.hashError)
        {
            RecordHashes(torrent);
        }
    }
    if (connectionTest)
        ReleaseConnectionTest(connectionTest->connectionId, ConnectionPhase::Cancelled);
    shutdownPhase = ShutdownPhase::Draining;
    phaseStarted.reset();
    FailReading(Failure(ErrorCode::ShuttingDown));
    Discard([](Preview const&) { return true; });
    log.Write("shutdown", "", "requested");
    shutdown = std::move(completion);
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
        if (startup == Startup::Ready && store.IsIdle() && log.IsFlushed())
        {
            Finish();
        }
        return;
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
                saveFailure.emplace();
                log.Write("shutdown", "", "rename_pending");
                Finish();
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
            if (!torrent.priorityReply)
            {
                continue;
            }
            if (std::chrono::steady_clock::now() - *phaseStarted < phaseTimeout)
            {
                pending = true;
                continue;
            }
            std::exchange(torrent.priorityReply, nullptr)(Failure(ErrorCode::RecoveryRequired));
            saveFailure.emplace();
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
        if (!pausing.empty())
        {
            if (std::chrono::steady_clock::now() - *phaseStarted < phaseTimeout)
            {
                return;
            }
            pausing.clear();
            saveFailure.emplace();
            log.Write("shutdown", "", "recovery_required");
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
        shutdownPhase = ShutdownPhase::Sampling;
        phaseStarted = std::chrono::steady_clock::now();
        transfersRequested = lt::clock_type::now();
        session->post_session_stats();
        log.Write("shutdown", "", "checkpoint");
        for (auto& [id, torrent] : torrents)
        {
            torrent.unsaved = true;
            torrent.retryAt = {};
            if (torrent.hashError)
            {
                saveFailure = torrent.hashError->detail;
            }
        }
    }
    if (shutdownPhase == ShutdownPhase::Sampling)
    {
        if (std::chrono::steady_clock::now() - *phaseStarted < phaseTimeout)
            return;
        saveFailure.emplace();
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
    // A payload job may still use the session, so it ends before the session.
    if (pending || !store.IsIdle() || !payload.IsIdle() || !log.IsFlushed())
    {
        return;
    }
    // Reporting first lets the desktop leave the screen before the teardown
    // waits on trackers.
    Finish();
    if (!saveFailure)
    {
        session.reset();
    }
}

void Engine::State::Finish()
{
    auto completion = std::exchange(shutdown, nullptr);
    completion(saveFailure);
}
}
