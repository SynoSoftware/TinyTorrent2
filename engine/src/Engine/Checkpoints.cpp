#include "Engine/State.h"
#include <libtorrent/write_resume_data.hpp>
#include <algorithm>

namespace tiny
{
namespace
{
// Store refuses work past its job limit, so at most this many checkpoints
// run at once.
constexpr std::ptrdiff_t checkpointLimit = 8;
constexpr auto checkpointRetry = std::chrono::seconds(30);
}

void Engine::State::WriteResume(std::string const& id, lt::add_torrent_params params,
    std::function<void(StorageOutcome)> completion)
{
    // A guarded magnet handle received only the info dictionary; root metadata
    // from a later torrent-file preview stays with the accepted torrent.
    if (auto found = torrents.find(id); found != torrents.end())
    {
        params.comment = found->second.comment;
        params.created_by = found->second.creator;
        params.creation_date = found->second.created;
    }
    store.Write(ResumeFile(id), [params = std::move(params)]
    {
        auto bytes = lt::write_resume_data_buf(params);
        return std::string(bytes.begin(), bytes.end());
    }, std::move(completion));
}

void Engine::State::CheckpointUnsaved()
{
    auto now = std::chrono::steady_clock::now();
    auto outstanding = std::count_if(torrents.begin(), torrents.end(),
        [](auto const& entry) { return entry.second.checkpointPhase != CheckpointPhase::Idle; });
    for (auto& [id, torrent] : torrents)
    {
        if (outstanding == checkpointLimit)
        {
            return;
        }
        if (torrent.unsaved && torrent.checkpointPhase == CheckpointPhase::Idle && now >= torrent.retryAt)
        {
            torrent.Checkpoint(stopping);
            ++outstanding;
        }
    }
}

// CheckpointUnsaved retries a failed checkpoint after checkpointRetry.
void Engine::State::FailCheckpoint(Torrent& torrent, Problem problem)
{
    if (!torrent.checkpointError || torrent.checkpointError->kind != problem.kind)
    {
        diagnostics.Write("checkpoint", torrent.identity, ToString(problem.kind));
    }
    if (stopping)
    {
        saveFailure = problem.detail;
    }
    torrent.unsaved = true;
    torrent.checkpointError = std::move(problem);
    torrent.retryAt = std::chrono::steady_clock::now() + checkpointRetry;
}

void Engine::State::On(lt::save_resume_data_alert const& alert)
{
    auto torrent = Find(alert.handle);
    if (!torrent)
    {
        return;
    }
    // Generating resume data clears libtorrent's dirty flags, so retain the
    // newest result until the current write ends rather than requesting it again.
    torrent->unsaved = false;
    if (torrent->checkpointPhase == CheckpointPhase::Writing)
    {
        torrent->pendingCheckpoint = alert.params;
        return;
    }
    SaveCheckpoint(*torrent, alert.params);
}

void Engine::State::SaveCheckpoint(Torrent& torrent, lt::add_torrent_params params)
{
    torrent.checkpointPhase = CheckpointPhase::Writing;
    auto id = torrent.identity;
    auto completion = [this, id, uploaded = params.total_uploaded, path = params.save_path,
        pieces = params.have_pieces, seeded = bool(params.flags & lt::torrent_flags::seed_mode)](StorageOutcome outcome)
    {
        auto found = torrents.find(id);
        if (found == torrents.end())
        {
            return;
        }
        auto& torrent = found->second;
        torrent.checkpointPhase = CheckpointPhase::Idle;
        if (auto next = std::exchange(torrent.pendingCheckpoint, std::nullopt))
        {
            SaveCheckpoint(torrent, std::move(*next));
        }
        if (!outcome.succeeded)
        {
            FailCheckpoint(torrent, {ProblemKind::StorageFailed, outcome.detail});
            return;
        }
        torrent.savedUploaded = uploaded;
        torrent.checkpointError.reset();
        if (!torrent.facts.verifyFiles || seeded) return;
        if (!changes.Queue([this, id, path, pieces]
        {
            auto found = torrents.find(id);
            if (found == torrents.end()) return;
            auto& torrent = found->second;
            if (!torrent.facts.verifyFiles || torrent.moving || !torrent.facts.moveDestination.empty() ||
                !SamePath(FullPath(Wide(path)), FullPath(Wide(torrent.facts.savePath)))) return;
            auto current = torrent.handle.status(lt::torrent_handle::query_pieces);
            if (current.state == lt::torrent_status::checking_files ||
                current.state == lt::torrent_status::checking_resume_data) return;
            for (int index = 0; index < pieces.size(); ++index)
            {
                auto piece = lt::piece_index_t(index);
                if (pieces[piece] && (index >= current.pieces.size() || !current.pieces[piece])) return;
            }
            auto document = Saved();
            document.torrents.at(id).verifyFiles = false;
            changes.Commit(document.ToJson(), [this, id](StorageOutcome saved)
            {
                if (saved.succeeded) torrents.at(id).facts.verifyFiles = false;
                else diagnostics.Write("move", id, "verification_checkpoint_failed");
            });
        })) diagnostics.Write("move", id, "verification_checkpoint_overloaded");
    };
    WriteResume(id, std::move(params), std::move(completion));
}

void Engine::State::On(lt::save_resume_data_failed_alert const& alert)
{
    auto torrent = Find(alert.handle);
    if (!torrent)
    {
        return;
    }
    // During a write this answers a request that dropped-alert recovery gave
    // up on. That recovery marked the torrent unsaved, so it checkpoints again
    // after the write.
    if (torrent->checkpointPhase == CheckpointPhase::Writing)
    {
        return;
    }
    torrent->checkpointPhase = CheckpointPhase::Idle;
    if (alert.error == lt::errors::resume_data_not_modified)
    {
        torrent->unsaved = false;
    }
    else
    {
        FailCheckpoint(*torrent, {ProblemKind::CheckpointFailed, alert.error.message()});
    }
}
}
