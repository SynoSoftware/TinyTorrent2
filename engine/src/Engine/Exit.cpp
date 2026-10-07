#include "Engine/State.h"
#include <algorithm>

namespace tt
{
namespace
{
// Exit gives each step this long: file priority edits to complete, then
// every torrent to pause.
constexpr auto stepTimeout = std::chrono::seconds(30);
}

void Engine::State::Shutdown(std::function<void(std::optional<std::string> failure)> completion)
{
    if (shutdown)
    {
        return;
    }
    // Exit after a failed final save starts its steps again.
    if (stopping && session)
    {
        saveFailure.reset();
        exitStep = ExitStep::Draining;
        pausing.clear();
    }
    for (auto& [id, torrent] : torrents)
    {
        if (torrent.hashError)
        {
            RecordHashes(torrent);
        }
    }
    stopping = true;
    stepStarted.reset();
    Discard([](Preview const&) { return true; });
    diagnostics.Write("shutdown", "", "requested");
    shutdown = std::move(completion);
}

// Exit waits for queued changes and additions, pauses every torrent,
// waits for outstanding checkpoints, and then saves every torrent a
// final time.
void Engine::State::Stop()
{
    if (!shutdown)
    {
        return;
    }
    if (!session)
    {
        if (startup == Startup::Ready && store.IsIdle() && diagnostics.IsFlushed())
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
            if (!stepStarted)
                stepStarted = std::chrono::steady_clock::now();
            if (std::chrono::steady_clock::now() - *stepStarted >= stepTimeout)
            {
                saveFailure.emplace();
                diagnostics.Write("shutdown", "", "rename_pending");
                Finish();
                return;
            }
        }
        bool unknown = (relocation && relocation->phase == RelocationPhase::Unknown) ||
            std::any_of(deletions.begin(), deletions.end(),
                [](auto const& deletion) { return deletion.phase == DeletionPhase::Unknown; });
        if (!unknown)
        {
            return;
        }
        saveFailure.emplace();
        Finish();
        return;
    }
    if (exitStep == ExitStep::Draining)
    {
        if (!stepStarted)
        {
            stepStarted = std::chrono::steady_clock::now();
        }
        bool pending = false;
        for (auto& [id, torrent] : torrents)
        {
            CompletePriorities(torrent);
            if (!torrent.priorityReply)
            {
                continue;
            }
            if (std::chrono::steady_clock::now() - *stepStarted < stepTimeout)
            {
                pending = true;
                continue;
            }
            std::exchange(torrent.priorityReply, nullptr)(Failure(ErrorCode::RecoveryRequired));
            saveFailure.emplace();
            diagnostics.Write("edit", id, "recovery_required");
        }
        if (pending)
        {
            return;
        }
        exitStep = ExitStep::Pausing;
        stepStarted = std::chrono::steady_clock::now();
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
    if (exitStep == ExitStep::Pausing)
    {
        if (!pausing.empty())
        {
            if (std::chrono::steady_clock::now() - *stepStarted < stepTimeout)
            {
                return;
            }
            pausing.clear();
            saveFailure.emplace();
            diagnostics.Write("shutdown", "", "recovery_required");
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
        exitStep = ExitStep::Saving;
        diagnostics.Write("shutdown", "", "checkpoint");
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
    if (pending || !store.IsIdle() || !diagnostics.IsFlushed())
    {
        return;
    }
    if (!saveFailure)
    {
        session.reset();
    }
    Finish();
}

void Engine::State::Finish()
{
    auto completion = std::exchange(shutdown, nullptr);
    completion(saveFailure);
}
}
