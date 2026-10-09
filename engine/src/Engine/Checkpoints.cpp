#include "Engine/State.h"
#include <libtorrent/write_resume_data.hpp>
#include <algorithm>

namespace tt
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
    // Starting after the last choice lets every eligible torrent get a slot,
    // even while earlier torrents become unsaved again faster than they save.
    auto entry = torrents.upper_bound(checkpointCursor);
    for (std::size_t visited = 0; visited < torrents.size() && outstanding < checkpointLimit; ++visited)
    {
        if (entry == torrents.end())
        {
            entry = torrents.begin();
        }
        auto& [id, torrent] = *entry;
        ++entry;
        if (torrent.unsaved && torrent.checkpointPhase == CheckpointPhase::Idle && now >= torrent.retryAt)
        {
            torrent.Checkpoint(shuttingDown);
            ++outstanding;
            checkpointCursor = id;
        }
    }
}

// CheckpointUnsaved retries a failed checkpoint after checkpointRetry.
void Engine::State::FailCheckpoint(Torrent& torrent, Problem problem)
{
    if (!torrent.checkpointError || torrent.checkpointError->kind != problem.kind)
    {
        log.Write("checkpoint", torrent.torrentId, ToString(problem.kind));
    }
    if (shuttingDown)
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
    auto id = torrent.torrentId;
    auto completion = [this, id, uploaded = params.total_uploaded, path = params.save_path,
        pieces = params.have_pieces, seeded = bool(params.flags & lt::torrent_flags::seed_mode)](StorageOutcome outcome)
    {
        auto found = torrents.find(id);
        if (found == torrents.end())
        {
            return;
        }
        auto& torrent = found->second;
        if (!outcome.succeeded)
        {
            FailCheckpoint(torrent, {ProblemKind::StorageFailed, outcome.detail});
            FinishCheckpoint(torrent);
            return;
        }
        torrent.savedUploaded = uploaded;
        torrent.checkpointError.reset();
        if (!torrent.facts.verifyFiles || seeded || torrent.pendingCheckpoint)
        {
            FinishCheckpoint(torrent);
            return;
        }
        auto handle = torrent.handle;
        auto verified = std::make_shared<bool>(false);
        payload.Run([handle, pieces, verified]
        {
            auto current = handle.status(lt::torrent_handle::query_pieces);
            if (current.state == lt::torrent_status::checking_files ||
                current.state == lt::torrent_status::checking_resume_data)
                return;
            for (int index = 0; index < pieces.size(); ++index)
            {
                auto piece = lt::piece_index_t(index);
                if (pieces[piece] && (index >= current.pieces.size() || !current.pieces[piece]))
                    return;
            }
            *verified = true;
        }, [this, id, path, handle, generation = torrent.generation, verified](StorageOutcome observed)
        {
            auto found = torrents.find(id);
            if (found == torrents.end())
                return;
            if (!observed.succeeded || !*verified || found->second.pendingCheckpoint)
            {
                FinishCheckpoint(found->second);
                return;
            }
            if (!changes.Queue([this, id, path, handle, generation]
            {
                auto found = torrents.find(id);
                if (found == torrents.end())
                {
                    return;
                }
                auto& torrent = found->second;
                if (torrent.handle != handle || torrent.generation != generation || torrent.pendingCheckpoint ||
                    torrent.deleted || !torrent.facts.verifyFiles || torrent.namePhase != NamePhase::Ready || torrent.moving ||
                    !torrent.facts.moveDestination.empty() || !SameFolder(path, torrent.facts.savePath))
                {
                    FinishCheckpoint(torrent);
                    return;
                }
                auto document = Saved();
                document.torrents.at(id).verifyFiles = false;
                changes.Commit(document.ToJson(), [this, id](StorageOutcome saved)
                {
                    auto& torrent = torrents.at(id);
                    bool refresh = saved.succeeded && torrent.pendingCheckpoint.has_value();
                    if (saved.succeeded)
                    {
                        torrent.facts.verifyFiles = false;
                        // A coalesced snapshot can predate the proof. Keep the proved
                        // file on disk and force a fresh successor instead.
                        torrent.pendingCheckpoint.reset();
                    }
                    else
                    {
                        log.Write("move", id, "verification_checkpoint_failed");
                    }
                    FinishCheckpoint(torrent);
                    if (refresh)
                        torrent.Checkpoint(true);
                });
            }))
            {
                log.Write("move", id, "verification_checkpoint_overloaded");
                FinishCheckpoint(found->second);
            }
        });
    };
    WriteResume(id, std::move(params), std::move(completion));
}

void Engine::State::FinishCheckpoint(Torrent& torrent)
{
    torrent.checkpointPhase = CheckpointPhase::Idle;
    if (auto next = std::exchange(torrent.pendingCheckpoint, std::nullopt))
        SaveCheckpoint(torrent, std::move(*next));
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
