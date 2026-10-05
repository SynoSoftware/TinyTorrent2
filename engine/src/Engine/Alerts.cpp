#include "Engine/State.h"
#include <libtorrent/torrent_info.hpp>
#include <algorithm>

namespace tiny
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
    if (auto updated = lt::alert_cast<lt::state_update_alert>(alert))
    {
        On(*updated);
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
            CompletePriorities(*torrent);
        }
    }
    else if (auto paused = lt::alert_cast<lt::torrent_paused_alert>(alert))
    {
        std::erase(pausing, paused->handle);
        if (relocation)
        {
            std::erase(relocation->waiting, paused->handle);
            ContinueMove();
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
            torrent->receivedPayload |=
                status.total_payload_download > torrent->status.total_payload_download;
            torrent->status = status;
            auto problem = torrent->Error();
            auto error = problem ? problem->detail : std::string();
            if (!error.empty() && error != torrent->notifiedError)
            {
                Notify(NoticeKind::Error, *torrent, error);
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
    auto status = torrent.handle.status(lt::torrent_handle::query_name);
    torrent.receivedPayload |= status.total_payload_download > torrent.status.total_payload_download;
    torrent.status = std::move(status);
    if (torrent.status.is_finished && torrent.receivedPayload)
    {
        torrent.receivedPayload = false;
        torrent.flushing = true;
    }
}

// libtorrent finishes a torrent when its pieces pass the hash check in memory,
// and writes them to disk afterwards. It posts this alert when that write ends,
// so a person who opens a completed file never finds it incomplete.
void Engine::State::On(lt::cache_flushed_alert const& alert)
{
    auto torrent = Find(alert.handle);
    if (!torrent || !torrent->flushing)
    {
        return;
    }
    torrent->flushing = false;
    Notify(NoticeKind::Completed, *torrent);
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
            torrent->status.info_hashes = alert.metadata->info_hashes();
            RecordHashes(*torrent);
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
        torrent->ApplyIntent();
        torrent->status.info_hashes = alert.handle.info_hashes();
        RecordHashes(*torrent);
    }
}

void Engine::State::On(lt::torrent_error_alert const& alert)
{
    if (auto torrent = Find(alert.handle))
    {
        diagnostics.Write("torrent", torrent->identity, Code(alert.error));
    }
}

void Engine::State::On(lt::file_error_alert const& alert)
{
    if (auto torrent = Find(alert.handle))
    {
        torrent->diskError = alert.error.message();
        if (torrent->priorityReply)
        {
            std::exchange(torrent->priorityReply, nullptr)(Failure("torrent_error", torrent->diskError));
        }
        diagnostics.Write("file", torrent->identity, Code(alert.error));
    }
}

// libtorrent dropped alerts, so the outcomes they carried are lost. Every
// torrent saves again, a resume request whose answer was lost is given up, and
// each addition that waited for a lost alert continues or fails. A resume file
// write already belongs to Store, which still completes it.
void Engine::State::On(lt::alerts_dropped_alert const&)
{
    if (relocation && relocation->phase == RelocationPhase::Moving)
    {
        auto& torrent = torrents.at(relocation->ids.at(relocation->current));
        if (!SamePath(FullPath(Wide(torrent.facts.savePath)), FullPath(Wide(relocation->destination))) &&
            SamePath(FullPath(Wide(torrent.handle.status().save_path)), FullPath(Wide(relocation->destination))))
            FinishMove(torrent.handle, std::nullopt);
        else
        {
            relocation->phase = RelocationPhase::Unknown;
            torrent.moveError = Problem{ProblemKind::MoveUncertain, {}};
            Notify(NoticeKind::Error, torrent);
        }
    }
    if (relocation && !relocation->waiting.empty() &&
        (relocation->phase == RelocationPhase::Preparing || relocation->phase == RelocationPhase::Waiting))
    {
        relocation->phase = RelocationPhase::Unknown;
        for (auto const& id : relocation->ids)
        {
            auto& torrent = torrents.at(id);
            torrent.moveError = Problem{ProblemKind::MoveUncertain, {}};
            Notify(NoticeKind::Error, torrent);
        }
    }
    if (deletion && !deletion->waiting.empty())
    {
        deletion->phase = DeletionPhase::Unknown;
        Notify(NoticeKind::DeleteFailed, deletion->names, {});
    }
    if (stopping && !pausing.empty())
    {
        saveFailure.emplace();
        pausing.clear();
        diagnostics.Write("shutdown", "", "recovery_required");
    }
    for (auto& [id, torrent] : torrents)
    {
        CompletePriorities(torrent);
        if (torrent.priorityReply)
        {
            std::exchange(torrent.priorityReply, nullptr)(Failure("recovery_required"));
        }
        if (torrent.checkpointPhase == CheckpointPhase::Requested)
        {
            torrent.checkpointPhase = CheckpointPhase::Idle;
        }
        torrent.unsaved = true;
        AwaitCompletion(torrent);
        // flush_cache posts a new cache_flushed_alert in place of a lost one.
        if (torrent.flushing)
        {
            torrent.handle.flush_cache();
        }
        if (!stopping)
        {
            torrent.ApplyIntent();
        }
        torrent.status.info_hashes = torrent.handle.info_hashes();
        RecordHashes(torrent);
    }
    auto live = session->get_torrents();
    std::vector<std::string> lost;
    for (auto& [id, addition] : additions)
    {
        if (addition.phase == AdditionPhase::Saving)
        {
            continue;
        }
        if (addition.phase == AdditionPhase::Moving)
        {
            // The move finished when the torrent already saves to its destination.
            if (addition.handle.is_valid() &&
                FullPath(Wide(addition.handle.status().save_path)) ==
                    FullPath(Wide(addition.params.save_path)))
            {
                SaveAddition(id, addition.handle);
            }
            else
            {
                lost.push_back(id);
            }
            continue;
        }
        // An accepted torrent keeps the address of the addition that created
        // it, and a later addition can reuse that address.
        auto found = std::find_if(live.begin(), live.end(),
            [this, &addition](lt::torrent_handle const& handle)
            { return handle.userdata().get<Addition>() == &addition && !Find(handle); });
        if (found != live.end())
        {
            SaveAddition(id, *found);
        }
        else
        {
            lost.push_back(id);
        }
    }
    for (auto const& id : lost)
    {
        Abandon(id, Failure("recovery_required"));
    }
    diagnostics.Write("alerts", "", "dropped");
}
}
