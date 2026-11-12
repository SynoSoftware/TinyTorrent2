#include "Engine/State.h"
#include <algorithm>

namespace tt
{
namespace
{
std::vector<DetailKind> Kinds(std::optional<TorrentView> view, bool metadata)
{
    // A request without a view, from Open torrent or Open folder, describes
    // the torrent with its General detail; those actions read no file.
    if (!view)
    {
        return Kinds(TorrentView::General, metadata);
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

std::set<lt::file_index_t> QuestionFiles(Torrent const& torrent)
{
    std::set<lt::file_index_t> files;
    for (auto const& [file, priority] : torrent.facts.Asked().files)
    {
        files.insert(file);
    }
    return files;
}
}

std::chrono::steady_clock::time_point Engine::State::Reader::Since() const
{
    return context ? started : started - answerAge;
}

bool Engine::State::Reader::Complete(Detail const& detail, bool metadata) const
{
    auto since = Since();
    auto kinds = Kinds(view, metadata);
    return std::all_of(kinds.begin(), kinds.end(), [&](DetailKind kind) { return detail.Has(kind, since); });
}

void Engine::State::Subscribe(std::string const& connectionId, std::int64_t context, TorrentView view,
    std::string const& section, std::vector<std::string> const& ids, std::optional<Loaded> const& loaded,
    Push const& push)
{
    // The key holds the context, so an earlier subscription's late message
    // never replaces this one's unsent observation of the same torrent.
    auto key = [](std::string const& id, std::int64_t context) { return id + ':' + std::to_string(context); };
    for (auto const& id : ids)
    {
        auto reply = [push, key, session = sessionId, id, context, section](Json response)
        {
            response["type"] = "observation";
            response["session_id"] = session;
            response["context"] = context;
            response["torrent_id"] = id;
            response["section"] = section;
            push.replace(key(id, context), std::move(response));
        };
        ReadDetail(Reader{connectionId, id, view, std::move(reply), {}, context, loaded});
    }
    // The earlier subscription's reads go only now, so no collection that
    // both show is ever without a read, and none loses its facts. Their
    // unsent observations go with them, because the window drops each one
    // it reads, and the new subscription's observations would wait behind
    // them.
    for (auto& [id, collection] : collections)
    {
        std::erase_if(collection.held, [&](Reader const& read)
        {
            if (read.connectionId != connectionId || !read.context || *read.context == context)
                return false;
            push.withdraw(key(read.torrentId, *read.context));
            return true;
        });
    }
    EndIdleCollections();
}

void Engine::State::ReadDetail(Reader read)
{
    // A display read answers at once with the facts the engine holds, which
    // are ready when they are recent enough for a complete read.
    if (read.context && !Refusal())
    {
        auto found = torrents.find(read.torrentId);
        if (found != torrents.end() && !found->second.waiting)
        {
            auto& torrent = found->second;
            auto metadata = torrent.handle.torrent_file();
            auto collection = collections.find(read.torrentId);
            Detail empty;
            auto const& detail = collection != collections.end() ? collection->second.detail : empty;
            auto recent = std::chrono::steady_clock::now() - answerAge;
            auto kinds = Kinds(read.view, bool(metadata));
            auto data = torrent.Describe(*read.view, detail, metadata);
            data["ready"] = std::all_of(kinds.begin(), kinds.end(),
                [&](DetailKind kind) { return detail.Has(kind, recent); });
            Respond(torrent, read, detail, std::move(data));
        }
    }
    CollectDetail(std::move(read));
}

void Engine::State::CollectDetail(Reader read)
{
    if (auto refusal = Refusal())
    {
        read.reply(Failure(*refusal));
        return;
    }
    auto found = torrents.find(read.torrentId);
    if (found == torrents.end())
    {
        read.reply(Failure(ErrorCode::TorrentRemoved));
        return;
    }
    auto& torrent = found->second;
    // A display read of a torrent that cannot be read yet keeps its place in
    // the subscription, and each refresh reads it again.
    if (auto failure = Unreadable(torrent))
    {
        read.reply(*failure);
        if (read.context)
        {
            collections[torrent.torrentId].held.push_back(std::move(read));
        }
        return;
    }
    read.started = std::chrono::steady_clock::now();
    auto& collection = collections[torrent.torrentId];
    collection.held.push_back(std::move(read));
    if (collecting.contains(torrent.torrentId))
    {
        Collect(torrent.torrentId);
        return;
    }
    Demand(torrent.torrentId, collection);
    Admit();
}

void Engine::State::Demand(std::string const& torrentId, Collection& collection)
{
    if (collection.queued && std::erase(refreshing, torrentId) == 0)
    {
        return;
    }
    collection.queued = true;
    demanded.push_back(torrentId);
}

std::optional<Json> Engine::State::Unreadable(Torrent const& torrent) const
{
    // libtorrent holds a torrent's detail, so one that waits for its add is
    // still starting.
    if (torrent.AwaitsAdd())
    {
        return Failure(ErrorCode::Starting);
    }
    if (torrent.HeldByAlias())
    {
        return Failure(ErrorCode::AliasConflict, torrent.conflict);
    }
    // libtorrent refused the add, and the person's Retry adds it again.
    if (torrent.waiting)
    {
        return Failure(ErrorCode::AddFailed, torrent.waiting->refusal->detail);
    }
    return std::nullopt;
}

void Engine::State::Respond(Torrent const& torrent, Reader const& read, Detail const& detail, Json data)
{
    if (!read.loaded)
    {
        read.reply(Success(std::move(data)));
        return;
    }
    for (auto key : {"wanted_count", "wanted_size", "folders", "files"})
        data[key] = nullptr;
    auto metadata = torrent.handle.torrent_file();
    auto priorities = torrent.ShownPriorities(detail);
    // Every such reply goes through the worker, so they reach the browser
    // in the order they were sent.
    std::optional<Listing> listing;
    if (metadata && priorities)
    {
        listing = Listing{metadata, Layout::Keep, std::move(*priorities),
            detail.Has(DetailKind::Progress) ? std::optional(detail.progress) : std::nullopt, true,
            QuestionFiles(torrent)};
    }
    trees.Run([listing = std::move(listing), loaded = *read.loaded]() mutable
    {
        Json values;
        if (!listing)
            return values;
        auto const& progress = listing->progress;
        auto count = static_cast<int>(listing->priorities.size());
        values["files"] = Json::array();
        for (auto file : loaded.files)
        {
            auto item = static_cast<int>(file);
            if (item >= 0 && item < count)
            {
                values["files"].push_back({{"index", item},
                    {"priority", static_cast<std::uint8_t>(listing->priorities[item])},
                    {"downloaded", progress && item < static_cast<int>(progress->size()) ? Json((*progress)[item]) :
                        Json()}, {"missing", listing->missing.contains(file)}});
            }
        }
        Tree tree(std::move(*listing));
        values.update(tree.Totals());
        values["folders"] = tree.Values(loaded.folders);
        return values;
    }, [data = std::move(data), reply = read.reply](std::expected<Json, StorageOutcome> values) mutable
    {
        // A worker that is too busy leaves the values unknown, as before
        // libtorrent reports them.
        if (values && values->is_object())
            data.update(*values);
        reply(Success(std::move(data)));
    });
}

std::expected<Listing, Outcome> Engine::State::Listed(Torrent const& torrent) const
{
    if (torrent.HeldByAlias())
    {
        return std::unexpected(Outcome{ErrorCode::AliasConflict, torrent.conflict});
    }
    auto metadata = torrent.Metadata();
    if (!metadata)
    {
        return std::unexpected(Outcome{ErrorCode::MetadataUnavailable});
    }
    std::optional<std::vector<std::int64_t>> progress;
    auto collection = collections.find(torrent.torrentId);
    if (collection != collections.end() && collection->second.detail.Has(DetailKind::Progress))
    {
        progress = collection->second.detail.progress;
    }
    return Listing{metadata, Layout::Keep, torrent.ChosenPriorities(metadata), std::move(progress), true,
        QuestionFiles(torrent)};
}

void Engine::State::ReadTree(Listing listing, Tree::Target target,
    std::function<void(std::expected<Json, Outcome>)> completion)
{
    trees.Run([listing = std::move(listing), target = std::move(target)]() mutable
    {
        return Tree(std::move(listing)).Describe(target);
    }, [completion](std::expected<std::optional<Json>, StorageOutcome> described)
    {
        if (!described)
            completion(std::unexpected(described.error().ToOutcome(ErrorCode::InvalidRequest)));
        else if (!*described)
            completion(std::unexpected(Outcome{ErrorCode::InvalidRequest}));
        else
            completion(std::move(**described));
    });
}

void Engine::State::NameFiles(Torrent const& torrent, Json& folder) const
{
    if (folder["files"].is_array())
    {
        for (auto& file : folder["files"])
            file["disk_path"] = torrent.DiskPath(lt::file_index_t(file["index"].get<int>()));
    }
    if (folder["folders"].is_array())
    {
        for (auto& child : folder["folders"])
            NameFiles(torrent, child);
    }
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
        handle.post_peer_info(true);
        break;
    case DetailKind::Progress:
        handle.post_file_progress({});
        break;
    case DetailKind::Priorities:
        handle.post_file_priorities();
        break;
    case DetailKind::Status:
        handle.post_status(detailQuery);
        break;
    case DetailKind::Availability:
        handle.post_piece_availability();
        break;
    case DetailKind::Downloading:
        handle.post_download_queue();
        break;
    }
}

void Engine::State::Admit()
{
    while (collecting.size() < collectionLimit)
    {
        auto& queue = !demanded.empty() ? demanded : refreshing;
        if (queue.empty())
        {
            return;
        }
        auto id = std::move(queue.front());
        queue.pop_front();
        auto found = collections.find(id);
        if (found == collections.end())
        {
            continue;
        }
        found->second.queued = false;
        collecting.insert(id);
        Collect(id);
    }
}

void Engine::State::Collect(std::string const& torrentId)
{
    auto found = torrents.find(torrentId);
    if (found == torrents.end())
    {
        FailReading(Failure(ErrorCode::TorrentRemoved), torrentId);
        return;
    }
    auto& torrent = found->second;
    if (auto failure = Unreadable(torrent))
    {
        // Each refresh tells the display reads again, so the window's round
        // does not wait for them; they stay for a later refresh.
        auto& held = collections.at(torrentId).held;
        for (auto const& read : held)
            read.reply(*failure);
        std::erase_if(held, [](Reader const& read) { return !read.context; });
        collecting.erase(torrentId);
        if (held.empty() && !collections.at(torrentId).queued)
            collections.erase(torrentId);
        Admit();
        return;
    }
    auto const& collection = collections.at(torrentId);
    auto metadata = bool(torrent.handle.torrent_file());
    for (auto const& read : collection.held)
    {
        auto since = read.Since();
        for (auto kind : Kinds(read.view, metadata))
        {
            if (!collection.detail.Has(kind, since))
                Query(torrent, kind);
        }
    }
    ContinueCollection(torrentId, false);
}

// Display reads receive the available facts whenever they change; complete
// reads are answered once their facts are complete.
void Engine::State::ContinueCollection(std::string const& torrentId, bool changed)
{
    auto found = collections.find(torrentId);
    if (found == collections.end())
    {
        return;
    }
    auto present = torrents.find(torrentId);
    if (present == torrents.end())
    {
        FailReading(Failure(ErrorCode::TorrentRemoved), torrentId);
        return;
    }
    auto& collection = found->second;
    auto& torrent = present->second;
    auto const& detail = collection.detail;
    auto metadata = torrent.handle.torrent_file();
    bool collects = collecting.contains(torrentId);
    bool waits = false;
    for (auto& read : std::exchange(collection.held, {}))
    {
        bool complete = read.Complete(detail, bool(metadata));
        if (read.context ? changed : complete)
        {
            auto data = read.view ? torrent.Describe(*read.view, detail, metadata)
                                  : torrent.Describe(detail, metadata);
            if (read.context)
                data["ready"] = complete;
            else if (read.view)
                data["session_id"] = sessionId;
            Respond(torrent, read, detail, std::move(data));
        }
        if (!complete && collects)
        {
            // A query given up, or answered before an edit made it obsolete,
            // is asked again.
            auto since = read.Since();
            for (auto kind : Kinds(read.view, bool(metadata)))
            {
                if (!detail.Has(kind, since))
                    Query(torrent, kind);
            }
        }
        waits |= !complete;
        if (read.context || !complete)
            collection.held.push_back(std::move(read));
    }
    bool empty = collection.held.empty();
    if (empty && !collection.queued)
    {
        collections.erase(found);
    }
    if ((empty || !waits) && collecting.erase(torrentId))
    {
        Admit();
    }
}

void Engine::State::PublishDetail()
{
    std::vector<std::string> due;
    for (auto& [id, collection] : collections)
    {
        if (collection.changed || collecting.contains(id))
            due.push_back(id);
    }
    for (auto const& id : due)
    {
        auto found = collections.find(id);
        if (found != collections.end())
            ContinueCollection(id, std::exchange(found->second.changed, false));
    }
}

void Engine::State::RefreshSubscriptions()
{
    auto now = std::chrono::steady_clock::now();
    for (auto& [id, collection] : collections)
    {
        bool displayed = false;
        for (auto& read : collection.held)
        {
            if (read.context)
            {
                read.started = now;
                displayed = true;
            }
        }
        if (displayed && !collecting.contains(id) && !std::exchange(collection.queued, true))
            refreshing.push_back(id);
    }
    Admit();
}

void Engine::State::Invalidate(std::string const& torrentId)
{
    // A removed torrent's names still change, and its file work compares
    // generations to notice that.
    auto found = FindWorking(torrentId);
    if (!found)
    {
        return;
    }
    ++found->generation;
    auto collection = collections.find(torrentId);
    if (collection == collections.end())
    {
        return;
    }
    // The subscription reads the torrent again, so the window never sees it
    // as it was before the change.
    collection->second.detail = {};
    collection->second.changed = false;
    auto now = std::chrono::steady_clock::now();
    for (auto& read : collection->second.held)
    {
        if (read.context)
            read.started = now;
    }
    if (collecting.contains(torrentId))
    {
        Collect(torrentId);
    }
    else
    {
        Demand(torrentId, collection->second);
        Admit();
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
    auto generation = query->second;
    bool current = generation == torrent->generation;
    torrent->queries.erase(query);
    if (current && observe)
        observe(*torrent);
    current &= generation == torrent->generation;
    auto collection = collections.find(torrent->torrentId);
    if (current && collection != collections.end())
    {
        store(collection->second.detail);
        collection->second.detail.received[kind] = std::chrono::steady_clock::now();
        collection->second.changed = true;
    }
    return current;
}

void Engine::State::FailReading(Json const& failure, std::optional<std::string> const& torrentId)
{
    for (auto& [id, collection] : collections)
    {
        if (!torrentId || id == *torrentId)
        {
            for (auto& read : std::exchange(collection.held, {}))
                read.reply(failure);
        }
    }
    EndIdleCollections();
}

void Engine::State::ReleaseReading(std::string const& connectionId, bool disconnected)
{
    auto client = [&connectionId, disconnected](Reader const& read)
    {
        return read.connectionId == connectionId && (disconnected || read.context.has_value());
    };
    for (auto& [id, collection] : collections)
        std::erase_if(collection.held, client);
    EndIdleCollections();
}

void Engine::State::EndIdleCollections()
{
    bool ended = false;
    for (auto found = collections.begin(); found != collections.end();)
    {
        if (!found->second.held.empty())
        {
            ++found;
            continue;
        }
        collecting.erase(found->first);
        found = collections.erase(found);
        ended = true;
    }
    if (ended)
    {
        std::erase_if(demanded, [this](std::string const& id) { return !collections.contains(id); });
        std::erase_if(refreshing, [this](std::string const& id) { return !collections.contains(id); });
        Admit();
    }
}

// The alert names only the kinds dropped, so a query of a dropped kind may
// never be answered: it is given up. Each complete read still waiting for one
// fails, to be read again, and display reads have it asked again. An answer
// that was not lost after all can then be taken as the next query's; that
// rare error is accepted rather than prevented.
void Engine::State::RecoverDetail(lt::alerts_dropped_alert const& alert)
{
    auto lost = [&alert](DetailKind kind) { return alert.dropped_alerts[AlertType(kind)]; };
    for (auto& [id, torrent] : torrents)
    {
        std::erase_if(torrent.queries, [&lost](auto const& query) { return lost(query.first); });
    }
    std::vector<std::string> affected(collecting.begin(), collecting.end());
    for (auto const& id : affected)
    {
        auto found = collections.find(id);
        auto torrent = torrents.find(id);
        if (found == collections.end() || torrent == torrents.end())
        {
            // ContinueCollection settles the reads of a torrent that is gone.
            ContinueCollection(id, false);
            collecting.erase(id);
            continue;
        }
        auto& collection = found->second;
        auto metadata = bool(torrent->second.handle.torrent_file());
        std::erase_if(collection.held, [&](Reader const& read)
        {
            if (read.context)
                return false;
            auto since = read.Since();
            auto kinds = Kinds(read.view, metadata);
            bool failed = std::any_of(kinds.begin(), kinds.end(),
                [&](DetailKind kind) { return lost(kind) && !collection.detail.Has(kind, since); });
            if (failed)
                read.reply(Failure(ErrorCode::RecoveryRequired));
            return failed;
        });
        ContinueCollection(id, false);
    }
    Admit();
}

void Engine::State::On(lt::tracker_list_alert const& alert)
{
    Receive(alert.handle, DetailKind::Trackers, [&alert](Detail& detail) { detail.trackers = alert.trackers; });
}

void Engine::State::On(lt::peer_info_alert& alert)
{
    Receive(alert.handle, DetailKind::Peers, [&alert](Detail& detail)
    {
        detail.peers = std::move(alert.peer_info);
        detail.knownPeers = std::move(alert.known_peers);
        for (auto& peer : detail.peers)
            peer.pieces.clear();
    });
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
    if ((torrent->priorityCompletion || torrent->piecesPending) &&
        (!metadata || alert.priorities != torrent->ChosenPriorities(metadata)))
        return;
    if (std::exchange(torrent->piecesPending, false))
        torrent->PrioritizePieces(alert.priorities);
    if (torrent->priorityCompletion)
    {
        torrent->unsaved = true;
        std::exchange(torrent->priorityCompletion, nullptr)({});
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
