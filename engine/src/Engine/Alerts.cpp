#include "Engine/State.h"
#include <libtorrent/torrent_info.hpp>
#include <libtorrent/session_stats.hpp>
#include <algorithm>

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
        On(static_cast<lt::save_resume_data_alert&>(*alert));
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
    case lt::torrent_removed_alert::alert_type:
        On(static_cast<lt::torrent_removed_alert const&>(*alert));
        break;
    case lt::torrent_delete_failed_alert::alert_type:
        On(static_cast<lt::torrent_delete_failed_alert const&>(*alert));
        break;
    case lt::tracker_list_alert::alert_type:
        On(static_cast<lt::tracker_list_alert const&>(*alert));
        break;
    case lt::peer_info_alert::alert_type:
        On(static_cast<lt::peer_info_alert&>(*alert));
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
    static auto received = lt::find_metric_idx("net.recv_payload_bytes");
    static auto sent = lt::find_metric_idx("net.sent_payload_bytes");
    auto values = alert.counters();
    transferred = {values[received], values[sent]};
    ObserveConnectionTest(alert);
    if (shutdownPhase == ShutdownPhase::Sampling && alert.timestamp() >= transfersRequested)
    {
        shutdownPhase = ShutdownPhase::Saving;
        SaveTransfers();
    }
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
    if (torrent->completion.Changed(alert.state, alert.prev_state))
        Invalidate(torrent->torrentId);
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
    AwaitQueueStatus(*torrent);
    torrent->piecesPending |= torrent->facts.firstLast;
    if (torrent->priorityCompletion || torrent->piecesPending)
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
    torrent->completion.Checked();
    Invalidate(torrent->torrentId);
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

StatusQuery Engine::State::Answered(lt::torrent_status const& status)
{
    if (!status.save_path.empty())
        return StatusQuery::Detail;
    return status.torrent_file.expired() ? StatusQuery::Routine : StatusQuery::Seed;
}

void Engine::State::On(lt::state_update_alert const& alert)
{
    bool changed = false;
    for (auto const& status : alert.status)
    {
        switch (Answered(status))
        {
        case StatusQuery::Routine:
            break;
        case StatusQuery::Seed:
            seedQueries.erase(status.handle);
            QuerySeeds();
            break;
        case StatusQuery::Detail:
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
                if (torrent.completion.NeedsSample())
                {
                    changed |= IsQueued(torrent.status.queue_position) != IsQueued(status.queue_position);
                    ObserveCompletion(torrent, status);
                }
            });
            continue;
        }
        if (auto torrent = Find(status.handle))
        {
            changed |= torrent->queueReport == QueueReport::Reported &&
                IsQueued(torrent->status.queue_position) != IsQueued(status.queue_position);
            torrent->Update(status);
            auto problem = torrent->Error();
            auto error = problem ? problem->detail : std::string();
            if (problem && !error.empty() && error != torrent->notifiedError)
            {
                Notify(ToastKind::Error, *torrent, error, problem->Code());
            }
            torrent->notifiedError = std::move(error);
        }
    }
    // A batch after an addition's files apply settles its queue state; a batch
    // that leaves it out found no change since its last report. A queued
    // addition is last in libtorrent's queue, so the accepted order applies again.
    for (auto& [id, torrent] : torrents)
    {
        if (torrent.queueReport == QueueReport::AwaitingStatus)
        {
            torrent.queueReport = QueueReport::Reported;
            changed = true;
        }
    }
    if (changed)
        queuePending = true;
    bool routine = std::all_of(alert.status.begin(), alert.status.end(), [](auto const& status)
    {
        return Answered(status) == StatusQuery::Routine;
    });
    if (routine)
    {
        statusOutstanding = false;
        auto interval = std::chrono::milliseconds(settings.refreshInterval);
        auto elapsed = statusAt - snapshotAt;
        bool due = elapsed >= interval;
        if (due)
            snapshotAt += interval * (elapsed / interval);
        if (std::exchange(snapshotPending, false) || due)
            PublishSnapshot();
        if (statusPending)
            RequestStatus();
    }
}

void Engine::State::QueryCompletions()
{
    if (IsShuttingDown())
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
        if (!torrent.completion.NeedsSample() || torrent.restore ||
            torrent.queries.contains(DetailKind::Status))
            continue;
        Query(torrent, DetailKind::Status);
        ++pending;
    }
}

void Engine::State::ObserveCompletion(Torrent& torrent, lt::torrent_status latest)
{
    bool flushes = torrent.completion.Observed(torrent.status, latest);
    latest.pieces = {};
    latest.verified_pieces = {};
    latest.renamed_files = {};
    latest.torrent_file.reset();
    latest.save_path.clear();
    torrent.Update(std::move(latest));
    // The natural flush may precede this observation, or its alert may be lost.
    if (flushes)
        torrent.handle.flush_cache();
}

// libtorrent finishes a torrent when its pieces pass the hash check in memory,
// and writes them to disk afterwards. It posts this alert when that write ends,
// so a person who opens a completed file never finds it incomplete.
void Engine::State::On(lt::cache_flushed_alert const& alert)
{
    FinishMove(alert.handle, {});
    auto torrent = Find(alert.handle);
    if (!torrent)
        return;
    if (torrent->cacheWrite == CacheWrite::Flushing)
    {
        torrent->cacheWrite = CacheWrite::Written;
        FinishFiles(*torrent);
        // Work that began during the write delays the rename, and the
        // resumed download writes again, so the next attempt flushes again.
        if (torrent->cacheWrite == CacheWrite::Written)
            torrent->cacheWrite = CacheWrite::Pending;
        if (!IsShuttingDown())
            torrent->ApplyIntent();
    }
    if (torrent->completion.Flushed())
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
    if (auto torrent = FindWorking(alert.handle))
    {
        // The next file query reads its names, which every file check waits
        // for.
        if (!torrent->restore && !torrent->names)
            torrent->filesPending = true;
        ContinueNames();
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
        // A failed file priority job raises this instead of file_prio_alert.
        AwaitQueueStatus(*torrent);
        if (torrent->priorityCompletion)
        {
            std::exchange(torrent->priorityCompletion, nullptr)({ErrorCode::TorrentError, torrent->diskError});
        }
        log.Write("file", torrent->torrentId, Code(alert.error));
    }
}

// libtorrent dropped alerts, so the outcomes they carried are lost. Every
// torrent saves again, a resume request whose answer was lost is forced again, and
// each addition that waited for a lost alert continues or fails. A resume file
// write already belongs to Store, which still completes it.
void Engine::State::On(lt::alerts_dropped_alert const& alert)
{
    if (shutdownPhase == ShutdownPhase::Sampling && alert.dropped_alerts[lt::session_stats_alert::alert_type])
        session->post_session_stats();
    if (alert.dropped_alerts[lt::state_update_alert::alert_type])
    {
        seedQueries.clear();
        statusOutstanding = false;
        RequestStatus();
    }
    RecoverDetail(alert);
    RecoverFiles();
    if (IsShuttingDown() && !pausing.empty())
    {
        FailFinalSave();
        pausing.clear();
        log.Write("shutdown", "", "recovery_required");
    }
    for (auto& [id, torrent] : torrents)
    {
        if (torrent.restore)
        {
            continue;
        }
        if (torrent.priorityCompletion)
        {
            std::exchange(torrent.priorityCompletion, nullptr)({ErrorCode::RecoveryRequired});
        }
        QueryPriorities(torrent);
        if (alert.dropped_alerts[lt::file_prio_alert::alert_type] ||
            alert.dropped_alerts[lt::file_error_alert::alert_type])
        {
            AwaitQueueStatus(torrent);
        }
        if (torrent.checkpointPhase == CheckpointPhase::Requested)
        {
            torrent.Checkpoint(true);
        }
        if (torrent.cacheWrite == CacheWrite::Flushing &&
            alert.dropped_alerts[lt::cache_flushed_alert::alert_type])
            torrent.handle.flush_cache();
        torrent.unsaved = true;
        // Only a lost state transition breaks the provenance of file alerts.
        // A sampled status cannot recover their original cause. File
        // completions earlier in this batch are unaffected: libtorrent drops
        // a high-priority state change only after the queue has passed the
        // limit for normal-priority file completions, so every completion
        // that arrived was posted before the lost transition.
        torrent.completion.Lost(alert.dropped_alerts[lt::state_changed_alert::alert_type]);
        Invalidate(torrent.torrentId);
        if (!IsShuttingDown())
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
