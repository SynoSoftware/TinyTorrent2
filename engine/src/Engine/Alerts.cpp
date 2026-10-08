#include "Engine/State.h"
#include <libtorrent/torrent_info.hpp>

namespace tt
{
namespace
{
std::string Code(lt::error_code const& error)
{
    return std::string(error.category().name()) + ":" + std::to_string(error.value());
}
}

void Engine::State::Handle(lt::alert* alert)
{
    if (auto external = lt::alert_cast<lt::external_ip_alert>(alert))
    {
        auto& address = external->external_address.is_v4() ? externalIpv4 : externalIpv6;
        address = external->external_address.to_string();
    }
    else if (auto updated = lt::alert_cast<lt::state_update_alert>(alert))
    {
        On(*updated);
    }
    else if (auto state = lt::alert_cast<lt::state_changed_alert>(alert))
    {
        if (auto torrent = Find(state->handle))
        {
            torrent->transferState = state->state;
            if (state->state == lt::torrent_status::downloading)
                ApplyQueue();
        }
    }
    else if (auto file = lt::alert_cast<lt::file_completed_alert>(alert))
    {
        On(*file);
    }
    else if (auto renamed = lt::alert_cast<lt::file_renamed_alert>(alert))
    {
        On(*renamed);
    }
    else if (auto failed = lt::alert_cast<lt::file_rename_failed_alert>(alert))
    {
        On(*failed);
    }
    else if (auto finished = lt::alert_cast<lt::torrent_finished_alert>(alert))
    {
        On(*finished);
    }
    else if (auto flushed = lt::alert_cast<lt::cache_flushed_alert>(alert))
    {
        On(*flushed);
    }
    else if (auto added = lt::alert_cast<lt::add_torrent_alert>(alert))
    {
        On(*added);
    }
    else if (auto moved = lt::alert_cast<lt::storage_moved_alert>(alert))
    {
        On(*moved);
    }
    else if (auto moveFailed = lt::alert_cast<lt::storage_moved_failed_alert>(alert))
    {
        On(*moveFailed);
    }
    else if (auto conflict = lt::alert_cast<lt::torrent_conflict_alert>(alert))
    {
        On(*conflict);
    }
    else if (auto received = lt::alert_cast<lt::metadata_received_alert>(alert))
    {
        On(*received);
    }
    else if (auto metadataFailed = lt::alert_cast<lt::metadata_failed_alert>(alert))
    {
        On(*metadataFailed);
    }
    else if (auto saved = lt::alert_cast<lt::save_resume_data_alert>(alert))
    {
        On(*saved);
    }
    else if (auto saveFailed = lt::alert_cast<lt::save_resume_data_failed_alert>(alert))
    {
        On(*saveFailed);
    }
    else if (auto torrentError = lt::alert_cast<lt::torrent_error_alert>(alert))
    {
        On(*torrentError);
    }
    else if (auto fileError = lt::alert_cast<lt::file_error_alert>(alert))
    {
        On(*fileError);
    }
    else if (auto priorities = lt::alert_cast<lt::file_prio_alert>(alert))
    {
        if (auto torrent = Find(priorities->handle))
        {
            if (torrent->facts.firstLast)
            {
                torrent->PrioritizePieces();
            }
            CompletePriorities(*torrent);
        }
    }
    else if (auto checked = lt::alert_cast<lt::torrent_checked_alert>(alert))
    {
        if (auto torrent = Find(checked->handle))
        {
            CompleteFiles(*torrent);
            FinishFiles(*torrent);
            if (torrent->facts.firstLast)
                torrent->PrioritizePieces();
        }
    }
    else if (auto paused = lt::alert_cast<lt::torrent_paused_alert>(alert))
    {
        std::erase(pausing, paused->handle);
        if (move)
        {
            std::erase(move->waiting, paused->handle);
            ContinueMove();
        }
        if (rename && rename->phase == RenamePhase::Waiting &&
            std::erase(rename->waiting, paused->handle))
        {
            ContinueRename();
        }
    }
    else if (auto deleted = lt::alert_cast<lt::torrent_deleted_alert>(alert))
    {
        On(*deleted);
    }
    else if (auto deleteFailed = lt::alert_cast<lt::torrent_delete_failed_alert>(alert))
    {
        On(*deleteFailed);
    }
    else if (auto dropped = lt::alert_cast<lt::alerts_dropped_alert>(alert))
    {
        On(*dropped);
    }
}

void Engine::State::On(lt::state_update_alert const& alert)
{
    for (auto const& status : alert.status)
    {
        if (auto torrent = Find(status.handle))
        {
            torrent->Update(status);
            auto problem = torrent->Error();
            auto error = problem ? problem->detail : std::string();
            if (!error.empty() && error != torrent->notifiedError)
            {
                Notify(NoticeKind::Error, *torrent, error, problem->Code());
            }
            torrent->notifiedError = std::move(error);
        }
    }
}

// libtorrent also reports a torrent as finished after a recheck, so only
// a torrent that received payload is notified as completed.
void Engine::State::On(lt::torrent_finished_alert const& alert)
{
    auto torrent = Find(alert.handle);
    if (!torrent)
    {
        return;
    }
    AwaitCompletion(*torrent);
}

void Engine::State::AwaitCompletion(Torrent& torrent)
{
    torrent.Update(torrent.handle.status(lt::torrent_handle::query_name));
    if (torrent.status.is_finished && torrent.receivedPayload)
    {
        torrent.receivedPayload = false;
        torrent.completionPhase = CompletionPhase::Flushing;
    }
}

// libtorrent finishes a torrent when its pieces pass the hash check in memory,
// and writes them to disk afterwards. It posts this alert when that write ends,
// so a person who opens a completed file never finds it incomplete.
void Engine::State::On(lt::cache_flushed_alert const& alert)
{
    if (rename && (rename->phase == RenamePhase::Flushing || rename->phase == RenamePhase::Recovering) &&
        std::erase(rename->waiting, alert.handle))
    {
        ContinueRename();
    }
    auto torrent = Find(alert.handle);
    if (!torrent || torrent->completionPhase != CompletionPhase::Flushing)
    {
        return;
    }
    torrent->completionPhase = CompletionPhase::Settling;
    FinishDownload(*torrent);
}

// Two torrents added under different info hashes turned out to be the
// same hybrid content, and libtorrent paused both. When one of them is
// only a preview, the preview gives way and the torrent continues.
void Engine::State::On(lt::torrent_conflict_alert const& alert)
{
    bool released = false;
    for (auto& [id, preview] : previews)
    {
        if (preview.handle == alert.handle || preview.handle == alert.conflicting_torrent)
        {
            preview.params.ti = alert.metadata;
            preview.params.info_hashes = alert.metadata->info_hashes();
            session->remove_torrent(preview.handle);
            preview.handle = {};
            released = true;
        }
    }
    for (auto handle : {alert.handle, alert.conflicting_torrent})
    {
        if (auto torrent = Find(handle))
        {
            RecordHashes(*torrent, alert.metadata->info_hashes());
            if (released)
            {
                handle.clear_error();
                torrent->ApplyIntent();
            }
            else
            {
                torrent->conflict = alert.message();
            }
        }
    }
}

void Engine::State::On(lt::metadata_received_alert const& alert)
{
    for (auto& [id, preview] : previews)
    {
        if (preview.handle == alert.handle)
        {
            UpdatePreview(preview);
            preview.error.clear();
        }
    }
    if (auto torrent = Find(alert.handle))
    {
        PrepareFiles(*torrent);
        torrent->ApplyIntent();
        RecordHashes(*torrent, alert.handle.info_hashes());
    }
}

void Engine::State::On(lt::torrent_error_alert const& alert)
{
    if (auto torrent = Find(alert.handle))
    {
        log.Write("torrent", torrent->torrentId, Code(alert.error));
    }
}

void Engine::State::On(lt::file_error_alert const& alert)
{
    if (auto torrent = Find(alert.handle))
    {
        torrent->diskError = alert.error.message();
        if (torrent->priorityReply)
        {
            std::exchange(torrent->priorityReply, nullptr)(Failure(ProblemKind::TorrentError, torrent->diskError));
        }
        log.Write("file", torrent->torrentId, Code(alert.error));
    }
}

// libtorrent dropped alerts, so the outcomes they carried are lost. Every
// torrent saves again, a resume request whose answer was lost is given up, and
// each addition that waited for a lost alert continues or fails. A resume file
// write already belongs to Store, which still completes it.
void Engine::State::On(lt::alerts_dropped_alert const&)
{
    RecoverFiles();
    if (shuttingDown && !pausing.empty())
    {
        saveFailure.emplace();
        pausing.clear();
        log.Write("shutdown", "", "recovery_required");
    }
    for (auto& [id, torrent] : torrents)
    {
        if (torrent.restore)
        {
            continue;
        }
        CompletePriorities(torrent);
        if (torrent.priorityReply)
        {
            std::exchange(torrent.priorityReply, nullptr)(Failure(ErrorCode::RecoveryRequired));
        }
        if (torrent.checkpointPhase == CheckpointPhase::Requested)
        {
            torrent.checkpointPhase = CheckpointPhase::Idle;
        }
        torrent.unsaved = true;
        AwaitCompletion(torrent);
        // flush_cache posts a new cache_flushed_alert in place of a lost one.
        if (torrent.completionPhase == CompletionPhase::Flushing)
        {
            torrent.handle.flush_cache();
        }
        if (!shuttingDown)
        {
            torrent.ApplyIntent();
        }
        RecordHashes(torrent, torrent.handle.info_hashes());
    }
    RecoverAdditions();
    ApplyQueue();
    log.Write("alerts", "", "dropped");
}
}
