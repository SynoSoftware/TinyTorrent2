#include "Engine/State.h"
#include <libtorrent/torrent_info.hpp>
#include <libtorrent/session_stats.hpp>
#include <algorithm>
#include <cwctype>

namespace tt
{
namespace
{
// Background storage conditions show only once they last this long, because
// most absent shares, such as a NAS waking up or Wi-Fi reconnecting, return
// sooner and the engine continues their torrents by itself.
constexpr auto noticeDelay = std::chrono::minutes(2);

std::string Code(lt::error_code const& error)
{
    return std::string(error.category().name()) + ":" + std::to_string(error.value());
}

std::optional<std::string> UnavailableRoot(std::string const& location, Problem const& problem)
{
    if (problem.kind != ProblemKind::DownloadPathMissing && problem.kind != ProblemKind::DownloadPathInvalid &&
        problem.kind != ProblemKind::DownloadPathUnavailable && problem.kind != ProblemKind::DiskFull)
        return std::nullopt;

    auto folder = Wide(location);
    if (folder.starts_with(LR"(\\?\UNC\)"))
        folder = LR"(\\)" + folder.substr(8);
    else if (folder.starts_with(LR"(\\?\)"))
        folder.erase(0, 4);
    auto path = std::filesystem::path(folder);
    path.make_preferred();
    auto root = path.root_path();
    // A UNC root_name names only the server; include its share.
    if (path.root_name().wstring().starts_with(LR"(\\)") && path.has_relative_path())
        root /= *path.relative_path().begin();
    if (root.empty())
        return std::nullopt;
    return Utf8(root.wstring());
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
    // libtorrent posts this for a paused check only after its last read.
    if (auto torrent = Find(alert.handle))
        torrent->checkReads = CheckReads::None;
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
            }, [this, &status, &changed, at = alert.timestamp()](Torrent& torrent)
            {
                if (!torrent.names)
                {
                    if (auto metadata = status.torrent_file.lock())
                        torrent.names = FileNames{std::move(metadata), status.renamed_files};
                }
                auto error = torrent.diskError ? torrent.diskError : status.errc;
                if (error ? Retryable(error) : bool(status.flags & lt::torrent_flags::upload_mode))
                    RecoverStorage(torrent, error);
                if (torrent.completion.NeedsSample())
                {
                    changed |= IsQueued(torrent.status.queue_position) != IsQueued(status.queue_position);
                    ObserveCompletion(torrent, status, at);
                }
            });
            continue;
        }
        if (auto torrent = Find(status.handle))
        {
            changed |= torrent->queueReport == QueueReport::Reported &&
                IsQueued(torrent->status.queue_position) != IsQueued(status.queue_position);
            torrent->Update(status, alert.timestamp());
            // A recheck passes through checking_resume_data while the reads of
            // the check it replaces may remain.
            if (status.state != lt::torrent_status::checking_files &&
                status.state != lt::torrent_status::checking_resume_data)
                torrent->checkReads = CheckReads::None;
            else if (torrent->ChecksFiles(Paused().has_value()) && torrent->checkReads == CheckReads::None)
                torrent->checkReads = CheckReads::Reading;
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

// Two conditions cover every torrent that its storage stopped: the torrents
// that wait for a location to answer, counted per drive or share, and the
// torrents that wait for the person's Retry, counted together so that one
// Retry continues them all.
void Engine::State::UpdateProblems()
{
    std::map<std::string, Toast> current;
    std::optional<Toast> stopped;
    for (auto& [id, torrent] : torrents)
    {
        auto problem = torrent.Error();
        if (problem && !problem->IsStorage() && problem != torrent.notifiedError)
            Notify(ToastKind::Error, torrent, problem->detail, problem->Code());
        torrent.notifiedError = problem;
        if (torrent.NeedsPerson())
        {
            auto cause = torrent.StorageProblem() ? *torrent.StorageProblem() : *torrent.moveError;
            if (!stopped)
            {
                stopped = Toast{.kind = ToastKind::Error, .name = torrent.Name(), .detail = cause.detail,
                    .code = cause.Code(), .torrentId = id, .folder = torrent.facts.savePath};
                continue;
            }
            // Several torrents name none of them, and a reason only when they
            // share it.
            ++stopped->count;
            stopped->name.clear();
            stopped->detail.clear();
            stopped->torrentId.clear();
            stopped->folder.clear();
            if (stopped->code != cause.Code())
                stopped->code.clear();
            continue;
        }
        auto storage = torrent.StorageProblem();
        if (!torrent.recovery || !storage)
            continue;
        if (auto root = UnavailableRoot(torrent.facts.savePath, *storage))
        {
            auto folder = Wide(*root);
            std::transform(folder.begin(), folder.end(), folder.begin(), [](wchar_t character)
                { return static_cast<wchar_t>(std::towlower(character)); });
            auto key = "location:" + Utf8(folder);
            auto [entry, inserted] = current.try_emplace(key, Toast{.kind = ToastKind::Error,
                .detail = storage->Code(), .code = "download_location_unavailable", .folder = *root, .count = 0});
            ++entry->second.count;
        }
    }
    if (stopped)
        current.emplace(Toast::Condition::stopped, std::move(*stopped));
    auto now = std::chrono::steady_clock::now();
    std::erase_if(lasting, [&current, now](auto const& entry)
        { return !current.contains(entry.first) && now - entry.second.seen >= noticeDelay; });
    for (auto const& [key, toast] : current)
        lasting.try_emplace(key, Lasting{now, now}).first->second.seen = now;
    std::erase_if(current, [this, now](auto const& entry)
        { return !problems.contains(entry.first) && now - lasting.at(entry.first).since < noticeDelay; });
    for (auto const& [key, toast] : current)
    {
        auto previous = problems.find(key);
        if (previous != problems.end() && previous->second.count == toast.count &&
            previous->second.code == toast.code && previous->second.detail == toast.detail &&
            previous->second.name == toast.name && previous->second.folder == toast.folder)
            continue;
        auto notice = toast;
        notice.condition = Toast::Condition{key, previous == problems.end() ? ProblemChange::Raised : ProblemChange::Updated};
        Notify(std::move(notice));
    }
    for (auto const& [key, toast] : problems)
    {
        if (current.contains(key))
            continue;
        auto notice = toast;
        notice.condition = Toast::Condition{key, ProblemChange::Resolved};
        Notify(std::move(notice));
    }
    problems = std::move(current);
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
        auto error = torrent.diskError ? torrent.diskError : torrent.status.errc;
        bool retries = !torrent.recovery &&
            (error ? Retryable(error) : bool(torrent.status.flags & lt::torrent_flags::upload_mode));
        if ((!torrent.completion.NeedsSample() && !retries) || torrent.restore ||
            torrent.queries.contains(DetailKind::Status))
            continue;
        Query(torrent, DetailKind::Status);
        ++pending;
    }
}

void Engine::State::ObserveCompletion(Torrent& torrent, lt::torrent_status latest, lt::time_point at)
{
    bool flushes = torrent.completion.Observed(torrent.status, latest);
    latest.pieces = {};
    latest.verified_pieces = {};
    latest.renamed_files = {};
    latest.torrent_file.reset();
    latest.save_path.clear();
    torrent.Update(std::move(latest), at);
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
        if (alert.timestamp() < torrent->diskClearedAt)
            return;
        torrent->diskError = alert.error;
        RecoverStorage(*torrent, alert.error);
        // A failed file priority job raises this instead of file_prio_alert.
        AwaitQueueStatus(*torrent);
        if (torrent->priorityCompletion)
        {
            std::exchange(torrent->priorityCompletion, nullptr)({ErrorCode::TorrentError, torrent->diskError.message()});
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
    // The lost pause confirmations end the wait for them, as their timeout
    // does.
    if (IsShuttingDown() && !pausing.empty())
    {
        pausing.clear();
        log.Write("shutdown", "", "recovery_required");
    }
    for (auto& [id, torrent] : torrents)
    {
        if (torrent.restore)
        {
            continue;
        }
        if (alert.dropped_alerts[lt::torrent_paused_alert::alert_type] && torrent.checkReads != CheckReads::None)
            RecoverStorage(torrent, torrent.diskError ? torrent.diskError : torrent.status.errc);
        if (alert.dropped_alerts[lt::file_error_alert::alert_type] && torrent.status.has_metadata)
            RecoverStorage(torrent, torrent.diskError);
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
