#include "Engine/State.h"
#include <algorithm>

namespace tt
{
namespace
{
std::vector<DetailKind> Kinds(std::optional<TorrentView> view, bool metadata)
{
    // A request without a view, from Open torrent or Open folder, describes
    // the torrent with its General and Files detail.
    if (!view)
    {
        auto kinds = Kinds(TorrentView::General, metadata);
        auto files = Kinds(TorrentView::Files, metadata);
        kinds.insert(kinds.end(), files.begin(), files.end());
        return kinds;
    }
    switch (*view)
    {
    case TorrentView::General:
    case TorrentView::Trackers:
        return {DetailKind::Trackers};
    case TorrentView::Peers:
        return {DetailKind::Peers};
    case TorrentView::Files:
        if (!metadata)
        {
            return {};
        }
        return {DetailKind::Progress, DetailKind::Priorities, DetailKind::Status};
    case TorrentView::Pieces:
        // Without metadata there are no pieces, and libtorrent never answers
        // the download queue query.
        if (!metadata)
        {
            return {};
        }
        return {DetailKind::Status, DetailKind::Availability, DetailKind::Downloading};
    }
    return {};
}

int AlertType(DetailKind kind)
{
    switch (kind)
    {
    case DetailKind::Trackers: return lt::tracker_list_alert::alert_type;
    case DetailKind::Peers: return lt::peer_info_alert::alert_type;
    case DetailKind::Progress: return lt::file_progress_alert::alert_type;
    case DetailKind::Priorities: return lt::file_priorities_alert::alert_type;
    case DetailKind::Status: return lt::state_update_alert::alert_type;
    case DetailKind::Availability: return lt::piece_availability_alert::alert_type;
    case DetailKind::Downloading: return lt::piece_info_alert::alert_type;
    }
    return 0;
}
}

void Engine::State::ReadDetail(Reading::Read read)
{
    if (read.context && reading)
    {
        auto replaced = [&read](Reading::Read const& previous)
        {
            return previous.context && previous.connectionId == read.connectionId;
        };
        std::erase_if(reading->held, replaced);
        std::erase_if(reading->waiting, replaced);
        if (reading->held.empty() && !reading->waiting.empty())
            EndReading();
    }
    if (read.context && !Refusal())
    {
        auto found = torrents.find(read.torrentId);
        if (found != torrents.end() && !found->second.deleted && !found->second.restore)
        {
            auto& torrent = found->second;
            read.started = std::chrono::steady_clock::now();
            auto metadata = torrent.handle.torrent_file();
            auto kinds = Kinds(read.view, bool(metadata));
            bool current = reading && reading->torrentId == read.torrentId;
            bool ready = current && std::all_of(kinds.begin(), kinds.end(),
                [this, &read](DetailKind kind)
                {
                    return reading->detail.Has(kind, read.started - Reading::answerAge);
                });
            Detail empty;
            auto const& detail = current ? reading->detail : empty;
            auto data = torrent.Describe(*read.view, read.includeFiles, detail, metadata,
                read.started - Reading::answerAge);
            data["session_id"] = sessionId;
            data["context"] = *read.context;
            data["ready"] = ready;
            read.reply(Success(std::move(data)));

            read.reply = [reply = std::move(read.reply), session = sessionId,
                torrentId = read.torrentId, context = *read.context](Json response)
            {
                response["type"] = "detail";
                response["session_id"] = session;
                response["torrent_id"] = torrentId;
                response["context"] = context;
                reply(std::move(response));
            };
        }
    }
    CollectDetail(std::move(read));
}

void Engine::State::CollectDetail(Reading::Read read)
{
    // A waiting read starts later, when the engine may have begun to close.
    if (auto refusal = Refusal())
    {
        read.reply(Failure(*refusal));
        return;
    }
    auto found = torrents.find(read.torrentId);
    if (found == torrents.end() || found->second.deleted)
    {
        read.reply(Failure(ErrorCode::TorrentRemoved));
        return;
    }
    auto& torrent = found->second;
    if (torrent.restore)
    {
        read.reply(Failure(ProblemKind::AliasConflict, torrent.conflict));
        return;
    }
    if (reading && reading->torrentId != torrent.torrentId)
    {
        reading->waiting.push_back(std::move(read));
        if (reading->held.empty())
        {
            EndReading();
        }
        return;
    }
    if (!reading)
    {
        reading = Reading{.torrentId = torrent.torrentId};
    }
    // Each read refreshes what it shows, so the detail stays current while the
    // window reads, and no query starts once it stops.
    for (auto kind : Kinds(read.view, bool(torrent.handle.torrent_file())))
    {
        Query(torrent, kind);
    }
    read.started = std::chrono::steady_clock::now();
    reading->held.push_back(std::move(read));
    ContinueReading();
}

void Engine::State::Query(Torrent& torrent, DetailKind kind)
{
    if (!torrent.queries.try_emplace(kind, torrent.generation).second)
    {
        return;
    }
    auto const& handle = torrent.handle;
    switch (kind)
    {
    case DetailKind::Trackers:
        handle.post_trackers();
        break;
    case DetailKind::Peers:
        handle.post_peer_info();
        break;
    case DetailKind::Progress:
        handle.post_file_progress({});
        break;
    case DetailKind::Priorities:
        handle.post_file_priorities();
        break;
    case DetailKind::Status:
        // The routine updates never ask for the save path, so it marks this
        // status as the answer.
        handle.post_status(lt::torrent_handle::query_name | lt::torrent_handle::query_save_path | lt::torrent_handle::query_renamed_files |
            lt::torrent_handle::query_pieces | lt::torrent_handle::query_torrent_file);
        break;
    case DetailKind::Availability:
        handle.post_piece_availability();
        break;
    case DetailKind::Downloading:
        handle.post_download_queue();
        break;
    }
}

// Sends the queries that the held reads still need, answers the reads whose
// detail is complete, and starts the waiting reads once no read is held.
void Engine::State::ContinueReading()
{
    if (!reading)
    {
        return;
    }
    auto& torrent = torrents.at(reading->torrentId);
    if (torrent.deleted)
    {
        FailReading(Failure(ErrorCode::TorrentRemoved));
        return;
    }
    auto const& detail = reading->detail;
    auto metadata = torrent.handle.torrent_file();
    std::vector<Reading::Read> ready;
    for (auto& read : std::exchange(reading->held, {}))
    {
        auto since = read.context ? read.started : read.started - Reading::answerAge;
        auto answered = [this, since](DetailKind kind) { return reading->detail.Has(kind, since); };
        auto kinds = Kinds(read.view, bool(metadata));
        if (std::all_of(kinds.begin(), kinds.end(), answered))
        {
            ready.push_back(std::move(read));
            continue;
        }
        for (auto kind : kinds)
        {
            if (!answered(kind))
            {
                Query(torrent, kind);
            }
        }
        reading->held.push_back(std::move(read));
    }
    for (auto& read : ready)
    {
        if (!read.view)
        {
            read.reply(Success(torrent.Describe(detail, metadata)));
            continue;
        }
        auto data = torrent.Describe(*read.view, read.includeFiles, detail, metadata);
        data["session_id"] = sessionId;
        if (read.context)
        {
            data["context"] = *read.context;
            data["ready"] = true;
        }
        read.reply(Success(std::move(data)));
    }
    if (reading->held.empty() && !reading->waiting.empty())
    {
        EndReading();
    }
}

void Engine::State::Invalidate(std::string const& torrentId)
{
    auto found = torrents.find(torrentId);
    if (found == torrents.end())
    {
        return;
    }
    ++found->second.generation;
    if (reading && reading->torrentId == torrentId)
    {
        reading->detail = {};
        ContinueReading();
    }
}

bool Engine::State::Receive(lt::torrent_handle const& handle, DetailKind kind,
    std::function<void(Detail&)> const& store, std::function<void(Torrent&)> const& observe)
{
    auto torrent = Find(handle);
    if (!torrent)
    {
        return false;
    }
    auto query = torrent->queries.find(kind);
    if (query == torrent->queries.end())
    {
        return false;
    }
    bool current = query->second == torrent->generation;
    torrent->queries.erase(query);
    if (current && observe)
        observe(*torrent);
    if (!reading || reading->torrentId != torrent->torrentId)
    {
        return current;
    }
    if (current)
    {
        store(reading->detail);
        reading->detail.received[kind] = std::chrono::steady_clock::now();
    }
    // An obsolete answer leaves its held reads waiting, so they ask again.
    ContinueReading();
    return current;
}

void Engine::State::FailReading(Json const& failure)
{
    if (!reading)
    {
        return;
    }
    for (auto& read : std::exchange(reading->held, {}))
    {
        read.reply(failure);
    }
    EndReading();
}

void Engine::State::ReleaseReading(std::string const& connectionId)
{
    if (!reading)
    {
        return;
    }
    auto client = [&connectionId](Reading::Read const& read) { return read.connectionId == connectionId; };
    std::erase_if(reading->held, client);
    std::erase_if(reading->waiting, client);
    if (reading->held.empty())
    {
        EndReading();
    }
}

void Engine::State::EndReading()
{
    auto waiting = std::move(reading->waiting);
    reading.reset();
    for (auto& read : waiting)
    {
        CollectDetail(std::move(read));
    }
}

// The alert names only the kinds dropped, so a query of a dropped kind may
// never be answered: it is given up, and each held read still waiting for one
// fails, to be read again. An answer that was not lost after all can then be
// taken as the next query's; that rare error is accepted rather than prevented.
void Engine::State::RecoverDetail(lt::alerts_dropped_alert const& alert)
{
    auto lost = [&alert](DetailKind kind) { return alert.dropped_alerts[AlertType(kind)]; };
    for (auto& [id, torrent] : torrents)
    {
        std::erase_if(torrent.queries, [&lost](auto const& query) { return lost(query.first); });
    }
    if (!reading)
    {
        return;
    }
    auto metadata = bool(torrents.at(reading->torrentId).handle.torrent_file());
    for (auto& read : std::exchange(reading->held, {}))
    {
        auto kinds = Kinds(read.view, metadata);
        auto since = read.context ? read.started : read.started - Reading::answerAge;
        if (std::any_of(kinds.begin(), kinds.end(),
            [this, since, &lost](DetailKind kind) { return lost(kind) && !reading->detail.Has(kind, since); }))
        {
            read.reply(Failure(ErrorCode::RecoveryRequired));
        }
        else
        {
            reading->held.push_back(std::move(read));
        }
    }
    ContinueReading();
}

void Engine::State::On(lt::tracker_list_alert const& alert)
{
    Receive(alert.handle, DetailKind::Trackers, [&alert](Detail& detail) { detail.trackers = alert.trackers; });
}

void Engine::State::On(lt::peer_info_alert const& alert)
{
    Receive(alert.handle, DetailKind::Peers, [&alert](Detail& detail) { detail.peers = alert.peer_info; });
}

void Engine::State::On(lt::file_progress_alert const& alert)
{
    Receive(alert.handle, DetailKind::Progress, [&alert](Detail& detail)
    {
        detail.progress.assign(alert.files.begin(), alert.files.end());
    });
}

void Engine::State::On(lt::file_priorities_alert const& alert)
{
    auto current = Receive(alert.handle, DetailKind::Priorities,
        [&alert](Detail& detail) { detail.priorities = alert.priorities; });
    auto torrent = Find(alert.handle);
    if (!torrent)
        return;
    if (!current)
    {
        QueryPriorities(*torrent);
        return;
    }
    auto metadata = torrent->handle.torrent_file();
    if ((torrent->priorityReply || torrent->piecesPending) &&
        (!metadata || alert.priorities != torrent->ChosenPriorities(metadata)))
        return;
    if (std::exchange(torrent->piecesPending, false))
        torrent->PrioritizePieces(alert.priorities);
    if (torrent->priorityReply)
    {
        torrent->unsaved = true;
        std::exchange(torrent->priorityReply, nullptr)(Success());
    }
}

void Engine::State::On(lt::piece_availability_alert const& alert)
{
    Receive(alert.handle, DetailKind::Availability, [&alert](Detail& detail)
    {
        detail.availability = alert.piece_availability;
    });
}

// Each piece's blocks point into the alert, so only their bytes are kept.
void Engine::State::On(lt::piece_info_alert const& alert)
{
    Receive(alert.handle, DetailKind::Downloading, [&alert](Detail& detail)
    {
        detail.downloading.clear();
        for (auto const& piece : alert.piece_info)
        {
            std::int64_t bytes = 0;
            for (int index = 0; index < piece.blocks_in_piece; ++index)
            {
                auto const& block = piece.blocks[index];
                bytes += block.state == lt::block_info::writing || block.state == lt::block_info::finished ?
                    block.block_size : block.bytes_progress;
            }
            detail.downloading[piece.piece_index] = bytes;
        }
    });
}
}
