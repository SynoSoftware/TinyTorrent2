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
    switch (alert->type())
    {
    case lt::session_stats_alert::alert_type:
        On(static_cast<lt::session_stats_alert const&>(*alert));
        break;
    case lt::external_ip_alert::alert_type:
        On(static_cast<lt::external_ip_alert const&>(*alert));
        break;
    case lt::state_update_alert::alert_type:
        On(static_cast<lt::state_update_alert const&>(*alert));
        break;
    case lt::state_changed_alert::alert_type:
        On(static_cast<lt::state_changed_alert const&>(*alert));
        break;
    case lt::file_completed_alert::alert_type:
        On(static_cast<lt::file_completed_alert const&>(*alert));
        break;
    case lt::file_renamed_alert::alert_type:
        On(static_cast<lt::file_renamed_alert const&>(*alert));
        break;
    case lt::file_rename_failed_alert::alert_type:
        On(static_cast<lt::file_rename_failed_alert const&>(*alert));
        break;
    case lt::cache_flushed_alert::alert_type:
        On(static_cast<lt::cache_flushed_alert const&>(*alert));
        break;
    case lt::add_torrent_alert::alert_type:
        On(static_cast<lt::add_torrent_alert const&>(*alert));
        break;
    case lt::storage_moved_alert::alert_type:
        On(static_cast<lt::storage_moved_alert const&>(*alert));
        break;
    case lt::storage_moved_failed_alert::alert_type:
        On(static_cast<lt::storage_moved_failed_alert const&>(*alert));
        break;
    case lt::torrent_conflict_alert::alert_type:
        On(static_cast<lt::torrent_conflict_alert const&>(*alert));
        break;
    case lt::metadata_received_alert::alert_type:
        On(static_cast<lt::metadata_received_alert const&>(*alert));
        break;
    case lt::metadata_failed_alert::alert_type:
        On(static_cast<lt::metadata_failed_alert const&>(*alert));
        break;
    case lt::save_resume_data_alert::alert_type:
        On(static_cast<lt::save_resume_data_alert const&>(*alert));
        break;
    case lt::save_resume_data_failed_alert::alert_type:
        On(static_cast<lt::save_resume_data_failed_alert const&>(*alert));
        break;
    case lt::torrent_error_alert::alert_type:
        On(static_cast<lt::torrent_error_alert const&>(*alert));
        break;
    case lt::file_error_alert::alert_type:
        On(static_cast<lt::file_error_alert const&>(*alert));
        break;
    case lt::file_prio_alert::alert_type:
        On(static_cast<lt::file_prio_alert const&>(*alert));
        break;
    case lt::torrent_checked_alert::alert_type:
        On(static_cast<lt::torrent_checked_alert const&>(*alert));
        break;
    case lt::torrent_paused_alert::alert_type:
        On(static_cast<lt::torrent_paused_alert const&>(*alert));
        break;
    case lt::torrent_deleted_alert::alert_type:
        On(static_cast<lt::torrent_deleted_alert const&>(*alert));
        break;
    case lt::torrent_delete_failed_alert::alert_type:
        On(static_cast<lt::torrent_delete_failed_alert const&>(*alert));
        break;
    case lt::tracker_list_alert::alert_type:
        On(static_cast<lt::tracker_list_alert const&>(*alert));
        break;
    case lt::peer_info_alert::alert_type:
        On(static_cast<lt::peer_info_alert const&>(*alert));
        break;
    case lt::file_progress_alert::alert_type:
        On(static_cast<lt::file_progress_alert const&>(*alert));
        break;
    case lt::file_priorities_alert::alert_type:
        On(static_cast<lt::file_priorities_alert const&>(*alert));
        break;
    case lt::piece_availability_alert::alert_type:
        On(static_cast<lt::piece_availability_alert const&>(*alert));
        break;
    case lt::piece_info_alert::alert_type:
        On(static_cast<lt::piece_info_alert const&>(*alert));
        break;
    case lt::alerts_dropped_alert::alert_type:
        On(static_cast<lt::alerts_dropped_alert const&>(*alert));
        break;
    default:
        break;
    }
}

void Engine::State::On(lt::session_stats_alert const& alert)
{
    ObserveConnectionTest(alert);
}

void Engine::State::On(lt::external_ip_alert const& alert)
{
    auto& address = alert.external_address.is_v4() ? externalIpv4 : externalIpv6;
    address = alert.external_address.to_string();
}

void Engine::State::On(lt::state_changed_alert const& alert)
{
    auto torrent = Find(alert.handle);
    if (!torrent)
    {
        return;
    }
    if (alert.state == lt::torrent_status::downloading)
    {
        if (torrent->completionPhase != CompletionPhase::Checking)
            torrent->completionPhase = CompletionPhase::Downloading;
        torrent->receivedPayload = false;
    }
    if (alert.state == lt::torrent_status::checking_files ||
        alert.state == lt::torrent_status::checking_resume_data)
    {
        torrent->receivedPayload = false;
        if (torrent->completionPhase == CompletionPhase::Downloading)
            torrent->completionPhase = CompletionPhase::Idle;
    }
    if (alert.state == lt::torrent_status::finished &&
        alert.prev_state == lt::torrent_status::downloading &&
        (torrent->completionPhase == CompletionPhase::Downloading ||
            torrent->completionPhase == CompletionPhase::Idle || torrent->receivedPayload))
    {
        torrent->completionPhase = CompletionPhase::Downloading;
        QueryCompletion(*torrent);
    }
    torrent->transferState = alert.state;
    if (alert.state == lt::torrent_status::downloading)
        queuePending = true;
}

void Engine::State::On(lt::file_prio_alert const& alert)
{
    auto torrent = Find(alert.handle);
    if (!torrent)
    {
        return;
    }
    torrent->piecesPending |= torrent->facts.firstLast;
    if (torrent->priorityReply || torrent->piecesPending)
    {
        Invalidate(torrent->torrentId);
        QueryPriorities(*torrent);
    }
}

void Engine::State::On(lt::torrent_checked_alert const& alert)
{
    auto torrent = Find(alert.handle);
    if (!torrent)
    {
        return;
    }
    if (torrent->completionPhase == CompletionPhase::Checking)
    {
        QueryCompletion(*torrent);
    }
    else
    {
        Invalidate(torrent->torrentId);
    }
    torrent->filesPending = true;
    if (torrent->facts.firstLast)
    {
        torrent->piecesPending = true;
        QueryPriorities(*torrent);
    }
}

void Engine::State::On(lt::torrent_paused_alert const& alert)
{
    std::erase(pausing, alert.handle);
}

void Engine::State::On(lt::state_update_alert const& alert)
{
    bool changed = false;
    for (auto const& status : alert.status)
    {
        if (status.save_path.empty() && !status.torrent_file.expired())
        {
            seedQueries.erase(status.handle);
            QuerySeeds();
        }
        // The save path marks an explicit query; routine status updates never request it.
        if (!status.save_path.empty())
        {
            Receive(status.handle, DetailKind::Status, [&status](Detail& detail)
            {
                detail.status = status;
                detail.status.renamed_files = {};
            }, [this, &status, &changed](Torrent& torrent)
            {
                if (!torrent.names)
                {
                    if (auto metadata = status.torrent_file.lock())
                        torrent.names = FileNames{std::move(metadata), status.renamed_files};
                }
                if (torrent.completionPending)
                {
                    changed |= !torrent.deleted &&
                        IsQueued(torrent.status.queue_position) != IsQueued(status.queue_position);
                    ObserveCompletion(torrent, status);
                }
            });
            continue;
        }
        if (auto torrent = Find(status.handle))
        {
            changed |= !torrent->deleted &&
                IsQueued(torrent->status.queue_position) != IsQueued(status.queue_position);
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
    if (changed)
        queuePending = true;
}

void Engine::State::QueryCompletion(Torrent& torrent)
{
    torrent.completionPending = true;
    Invalidate(torrent.torrentId);
}

void Engine::State::QueryCompletions()
{
    if (shuttingDown)
        return;
    auto pending = std::count_if(torrents.begin(), torrents.end(), [](auto const& entry)
    {
        return entry.second.queries.contains(DetailKind::Status);
    });
    // A dropped batch can affect every torrent; recovery must not fill
    // the alert queue again.
    for (auto& [id, torrent] : torrents)
    {
        if (pending >= 64)
            break;
        if (!torrent.completionPending || torrent.deleted || torrent.restore ||
            torrent.queries.contains(DetailKind::Status))
            continue;
        Query(torrent, DetailKind::Status);
        ++pending;
    }
}

void Engine::State::ObserveCompletion(Torrent& torrent, lt::torrent_status latest)
{
    torrent.completionPending = false;
    bool checking = latest.state == lt::torrent_status::checking_files ||
        latest.state == lt::torrent_status::checking_resume_data;
    if (torrent.completionPhase == CompletionPhase::Checking && !checking)
        torrent.completionPhase = latest.is_finished ? CompletionPhase::Checked : CompletionPhase::Idle;
    if (latest.state == lt::torrent_status::downloading)
        torrent.completionPhase = CompletionPhase::Downloading;
    // Restarting resets the counter. This fresh read can recognize a download
    // that finishes between samples; a routine status cannot establish a reset.
    torrent.receivedPayload |= torrent.completionPhase == CompletionPhase::Downloading &&
        latest.total_payload_download > 0 && latest.total_payload_download != torrent.status.total_payload_download;
    latest.pieces = {};
    latest.verified_pieces = {};
    latest.renamed_files = {};
    latest.torrent_file.reset();
    latest.save_path.clear();
    torrent.Update(std::move(latest));
    if (checking)
    {
        torrent.receivedPayload = false;
        if (torrent.completionPhase == CompletionPhase::Downloading)
            torrent.completionPhase = CompletionPhase::Idle;
        return;
    }
    if (torrent.status.is_finished && torrent.completionPhase == CompletionPhase::Downloading)
    {
        torrent.completionPhase = std::exchange(torrent.receivedPayload, false) ?
            CompletionPhase::Flushing : CompletionPhase::Idle;
    }
    // The natural flush may precede this observation, or its alert may be lost.
    if (torrent.completionPhase == CompletionPhase::Flushing)
        torrent.handle.flush_cache();
}

// libtorrent finishes a torrent when its pieces pass the hash check in memory,
// and writes them to disk afterwards. It posts this alert when that write ends,
// so a person who opens a completed file never finds it incomplete.
void Engine::State::On(lt::cache_flushed_alert const& alert)
{
    FinishMove(alert.handle, std::nullopt);
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
        Invalidate(torrent->torrentId);
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
void Engine::State::On(lt::alerts_dropped_alert const& alert)
{
    if (alert.dropped_alerts[lt::state_update_alert::alert_type])
    {
        seedQueries.clear();
    }
    RecoverDetail(alert);
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
        if (torrent.priorityReply)
        {
            std::exchange(torrent.priorityReply, nullptr)(Failure(ErrorCode::RecoveryRequired));
        }
        QueryPriorities(torrent);
        if (torrent.checkpointPhase == CheckpointPhase::Requested)
        {
            torrent.checkpointPhase = CheckpointPhase::Idle;
        }
        torrent.unsaved = true;
        QueryCompletion(torrent);
        // The latest status cannot establish the origin of queued file alerts.
        torrent.transferState.reset();
        if (!shuttingDown)
        {
            torrent.ApplyIntent();
        }
        RecordHashes(torrent, torrent.handle.info_hashes());
    }
    RecoverAdditions();
    queuePending = true;
    log.Write("alerts", "", "dropped");
}
}
