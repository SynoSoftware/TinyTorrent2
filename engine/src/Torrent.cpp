#include "Torrent.h"
#include <libtorrent/hex.hpp>
#include <libtorrent/magnet_uri.hpp>
#include <libtorrent/peer_info.hpp>
#include <libtorrent/torrent_info.hpp>
#include <algorithm>
#include <stdexcept>

namespace tt
{
namespace
{
// The changes since the last checkpoint that make a new one worth writing.
constexpr lt::resume_data_flags_t dirty = lt::torrent_handle::if_download_progress |
    lt::torrent_handle::if_config_changed | lt::torrent_handle::if_state_changed |
    lt::torrent_handle::if_metadata_changed;

char const* ToString(Status status)
{
    switch (status)
    {
    case Status::Moving: return "moving";
    case Status::Error: return "error";
    case Status::Paused: return "paused";
    case Status::Checking: return "checking";
    case Status::Metadata: return "metadata";
    case Status::Queued: return "queued";
    case Status::Seeding: return "seeding";
    case Status::Completed: return "completed";
    case Status::Downloading: return "downloading";
    }
    return "";
}
}

char const* ToString(ProblemKind kind)
{
    switch (kind)
    {
    case ProblemKind::MoveInterrupted: return "move_interrupted";
    case ProblemKind::MoveFailed: return "move_failed";
    case ProblemKind::DestinationExists: return "destination_exists";
    case ProblemKind::MoveUncertain: return "move_uncertain";
    case ProblemKind::AliasConflict: return "alias_conflict";
    case ProblemKind::TorrentError: return "torrent_error";
    case ProblemKind::StorageFailed: return "storage_failed";
    case ProblemKind::StorageOverloaded: return "storage_overloaded";
    case ProblemKind::CheckpointFailed: return "checkpoint_failed";
    }
    return "";
}

std::vector<std::string> Hashes(lt::info_hash_t const& hashes)
{
    std::vector<std::string> values;
    if (hashes.has_v1())
    {
        values.push_back(lt::aux::to_hex(hashes.v1.to_string()));
    }
    if (hashes.has_v2())
    {
        values.push_back(lt::aux::to_hex(hashes.v2.to_string()));
    }
    return values;
}

// Content without a name yet, such as a magnet link before its metadata,
// shows its first hash.
std::string Name(std::string name, std::vector<std::string> const& hashes)
{
    if (name.empty() && !hashes.empty())
    {
        name = hashes.front();
    }
    return name;
}

// Padding files only align pieces, so they never download.
lt::download_priority_t DefaultPriority(lt::file_storage const& files, lt::file_index_t index)
{
    return files.pad_file_at(index) ? lt::dont_download : lt::default_priority;
}

std::vector<lt::download_priority_t> DefaultPriorities(lt::file_storage const& files)
{
    std::vector<lt::download_priority_t> priorities;
    for (auto index : files.file_range())
    {
        priorities.push_back(DefaultPriority(files, index));
    }
    return priorities;
}

Json Files(std::shared_ptr<lt::torrent_info const> const& metadata)
{
    Json files = Json::array();
    if (!metadata)
    {
        return files;
    }
    auto const& storage = metadata->layout();
    for (auto index : storage.file_range())
    {
        files.push_back({{"index", static_cast<int>(index)}, {"path", storage.file_path(index)},
            {"size", storage.file_size(index)}, {"padding", storage.pad_file_at(index)},
            {"priority", static_cast<std::uint8_t>(DefaultPriority(storage, index))}});
    }
    return files;
}

std::vector<std::filesystem::path> Paths(lt::torrent_info const& metadata)
{
    std::vector<std::filesystem::path> paths;
    auto const& files = metadata.layout();
    for (auto index : files.file_range())
    {
        if (!files.pad_file_at(index))
        {
            paths.push_back(Wide(files.file_path(index)));
        }
    }
    return paths;
}

std::vector<std::string> Urls(std::vector<lt::announce_entry> const& trackers)
{
    std::vector<std::string> urls;
    for (auto const& tracker : trackers)
    {
        urls.push_back(tracker.url);
    }
    return urls;
}

Json Facts::ToJson() const
{
    Json chosen = Json::array();
    for (auto priority : priorities)
    {
        chosen.push_back(static_cast<std::uint8_t>(priority));
    }
    Json saved = {
        {"save_path", savePath},
        {"move_destination", moveDestination},
        {"verify_files", verifyFiles},
        {"paused", intent == Intent::Paused},
        {"forced", intent == Intent::Forced},
        {"ignores_seed_limits", ignoresSeedLimits},
        {"added", added},
        {"priorities", std::move(chosen)},
        {"hashes", hashes}};
    if (trackers)
    {
        saved["trackers"] = Json::array();
        for (auto const& tracker : *trackers)
        {
            saved["trackers"].push_back({{"url", tracker.url}, {"tier", tracker.tier}});
        }
    }
    return saved;
}

Facts Facts::Read(Json const& saved)
{
    Facts facts;
    facts.savePath = saved.at("save_path").get<std::string>();
    facts.moveDestination = saved.value("move_destination", "");
    facts.verifyFiles = saved.value("verify_files", false);
    if (saved.at("paused").get<bool>())
    {
        facts.intent = Intent::Paused;
    }
    else if (saved.value("forced", false))
    {
        facts.intent = Intent::Forced;
    }
    facts.added = saved.at("added").get<std::int64_t>();
    facts.ignoresSeedLimits = saved.value("ignores_seed_limits", false);
    facts.priorities = ReadPriorities(saved.at("priorities"));
    facts.hashes = saved.value("hashes", std::vector<std::string>{});
    if (saved.contains("trackers"))
    {
        facts.trackers.emplace();
        for (auto const& entry : saved.at("trackers"))
        {
            lt::announce_entry tracker(entry.is_string() ? entry.get<std::string>() :
                entry.at("url").get<std::string>());
            tracker.tier = entry.is_string() ? 0 : entry.at("tier").get<std::uint8_t>();
            facts.trackers->push_back(std::move(tracker));
        }
    }
    return facts;
}

std::vector<lt::download_priority_t> ReadPriorities(Json const& values)
{
    if (!values.is_array())
    {
        throw std::invalid_argument("File priorities are not a list.");
    }
    std::vector<lt::download_priority_t> priorities;
    for (auto const& value : values)
    {
        if (!value.is_number_integer() || value < 0 || value > 255)
        {
            throw std::invalid_argument("A file priority is not a whole number from 0 to 255.");
        }
        priorities.emplace_back(value.get<std::uint8_t>());
    }
    return priorities;
}

std::string Torrent::Name() const
{
    return tt::Name(status.name, Hashes());
}

std::vector<std::string> Torrent::Hashes() const
{
    auto hashes = tt::Hashes(status.info_hashes);
    for (auto const& hash : facts.hashes)
    {
        if (std::find(hashes.begin(), hashes.end(), hash) == hashes.end())
        {
            hashes.push_back(hash);
        }
    }
    return hashes;
}

// The folder that holds the torrent's files: its save path, or the top
// folder that all its files share.
std::string Torrent::Folder() const
{
    std::filesystem::path folder = Wide(facts.savePath);
    auto metadata = handle.torrent_file();
    auto paths = metadata ? Paths(*metadata) : std::vector<std::filesystem::path>{};
    if (paths.size() == 1)
    {
        return Utf8((folder / paths.front()).parent_path().wstring());
    }
    if (!paths.empty() && paths.front().has_parent_path())
    {
        auto root = *paths.front().begin();
        if (std::all_of(paths.begin(), paths.end(),
            [&root](auto const& path) { return *path.begin() == root; }))
        {
            folder /= root;
        }
    }
    return Utf8(folder.wstring());
}

// The problem that stops the torrent, if any. libtorrent switches a torrent
// to upload mode when a disk write fails, so a torrent that still downloads
// stops there.
std::optional<Problem> Torrent::Error() const
{
    if (moveError)
    {
        return moveError;
    }
    if (!moving && !facts.moveDestination.empty())
    {
        return Problem{ProblemKind::MoveInterrupted, facts.moveDestination};
    }
    if (!conflict.empty())
    {
        return Problem{ProblemKind::AliasConflict, conflict};
    }
    if (status.errc)
    {
        return Problem{ProblemKind::TorrentError, status.errc.message()};
    }
    if (bool(status.flags & lt::torrent_flags::upload_mode) && !status.is_seeding)
    {
        return Problem{ProblemKind::TorrentError, diskError};
    }
    return std::nullopt;
}

// A row shows one problem: the first of these that applies.
std::optional<Problem> Torrent::Diagnose() const
{
    if (auto error = Error())
    {
        return error;
    }
    if (hashError)
    {
        return hashError;
    }
    return checkpointError;
}

Status Torrent::Classify(bool allPaused) const
{
    if (moving)
    {
        return Status::Moving;
    }
    if (Error())
    {
        return Status::Error;
    }
    if (facts.intent == Intent::Paused || allPaused)
    {
        return Status::Paused;
    }
    if (status.state == lt::torrent_status::checking_files ||
        status.state == lt::torrent_status::checking_resume_data)
    {
        return Status::Checking;
    }
    if (!status.has_metadata)
    {
        return Status::Metadata;
    }
    if (bool(status.flags & lt::torrent_flags::paused))
    {
        return Status::Queued;
    }
    if (flushing)
    {
        return Status::Downloading;
    }
    if (status.is_seeding)
    {
        return Status::Seeding;
    }
    if (status.is_finished)
    {
        return Status::Completed;
    }
    return Status::Downloading;
}

bool Torrent::IsChanged() const
{
    return status.all_time_upload != savedUploaded || bool(status.need_save_resume_data & dirty);
}

void Torrent::Update(lt::torrent_status latest)
{
    receivedPayload |= latest.total_payload_download > status.total_payload_download;
    status = std::move(latest);
}

Json Torrent::Describe() const
{
    auto data = facts.ToJson();
    data.update(Describe(TorrentView::General, false));
    data.update(Describe(TorrentView::Files, false));
    if (facts.trackers)
    {
        data["trackers"] = Urls(*facts.trackers);
    }
    return data;
}

namespace
{
Json DescribePeers(lt::torrent_handle const& handle)
{
    std::vector<lt::peer_info> infos;
    handle.get_peer_info(infos);
    Json data;
    data["peers"] = Json::array();
    for (auto const& peer : infos)
    {
        std::string address;
        auto transport = bool(peer.flags & lt::peer_info::utp_socket) ? "utp" : "tcp";
        if (bool(peer.flags & lt::peer_info::i2p_socket))
        {
            transport = "i2p";
#if TORRENT_USE_I2P
            address = lt::aux::to_hex(peer.i2p_destination().to_string());
#endif
        }
        else
        {
            auto endpoint = peer.remote_endpoint();
            address = endpoint.address().to_string();
            address = (endpoint.address().is_v6() ? "[" + address + "]" : address) +
                ":" + std::to_string(endpoint.port());
        }
        if (bool(peer.connection_type & (lt::peer_info::web_seed | lt::peer_info::http_seed)))
        {
            transport = bool(peer.flags & lt::peer_info::ssl_socket) ? "https" : "http";
        }
        data["peers"].push_back({{"endpoint", address}, {"client", peer.client},
            {"transport", transport},
            {"incoming", !bool(peer.flags & lt::peer_info::outgoing_connection)},
            {"encrypted", bool(peer.flags & (lt::peer_info::rc4_encrypted |
                lt::peer_info::plaintext_encrypted | lt::peer_info::ssl_socket))},
            {"progress", peer.progress_ppm / 1'000'000.0},
            {"download_rate", peer.payload_down_speed}, {"upload_rate", peer.payload_up_speed},
            {"downloaded", peer.total_download}, {"uploaded", peer.total_upload}});
    }
    return data;
}

// One tracker's row. `now` and `wall` are the same moment on libtorrent's
// clock and the system clock, read once for the whole list.
Json DescribeTracker(lt::announce_entry const& tracker, lt::info_hash_t const& hashes,
    lt::time_point now, std::time_t wall)
{
    bool enabled = tracker.endpoints.empty();
    bool updating = false;
    bool working = false;
    bool failed = false;
    int seeds = -1;
    int leechers = -1;
    int downloaded = -1;
    std::int64_t next = 0;
    std::string message;
    for (auto const& endpoint : tracker.endpoints)
    {
        if (!endpoint.enabled)
        {
            continue;
        }
        for (auto version : {lt::protocol_version::V1, lt::protocol_version::V2})
        {
            if (!hashes.has(version))
            {
                continue;
            }
            auto const& state = endpoint.info_hashes[version];
            auto usable = tracker.fail_limit == 0 || state.fails < tracker.fail_limit;
            enabled |= usable;
            updating |= usable && state.updating;
            working |= usable && state.start_sent && !state.last_error && state.fails == 0;
            failed |= bool(state.last_error) || state.fails > 0;
            seeds = std::max(seeds, state.scrape_complete);
            leechers = std::max(leechers, state.scrape_incomplete);
            downloaded = std::max(downloaded, state.scrape_downloaded);
            if (usable && state.next_announce != (lt::time_point32::min)())
            {
                auto time = wall + std::max<std::int64_t>(0,
                    std::chrono::duration_cast<std::chrono::seconds>(
                        std::max(state.next_announce, state.min_announce) - now).count());
                if (next == 0 || time < next)
                {
                    next = time;
                }
            }
            if (!state.message.empty())
            {
                message = state.message;
            }
            else if (state.last_error)
            {
                message = state.last_error.message();
            }
        }
    }
    auto state = !enabled ? "disabled" : updating ? "announcing" :
        working ? "working" : failed ? "error" : "waiting";
    return {{"url", tracker.url}, {"tier", tracker.tier}, {"status", state}, {"seeds", seeds},
        {"leechers", leechers}, {"downloaded", downloaded}, {"next_announce", next},
        {"message", message}};
}

Json DescribeTrackers(lt::torrent_handle const& handle)
{
    Json data;
    data["trackers"] = Json::array();
    auto now = lt::clock_type::now();
    auto wall = std::chrono::system_clock::to_time_t(std::chrono::system_clock::now());
    auto hashes = handle.info_hashes();
    for (auto const& tracker : handle.trackers())
    {
        data["trackers"].push_back(DescribeTracker(tracker, hashes, now, wall));
    }
    return data;
}

Json DescribeGeneral(Torrent const& torrent, std::shared_ptr<lt::torrent_info const> const& metadata)
{
    auto name = torrent.Name();
    Json data;
    data["name"] = name;
    data["folder"] = torrent.Folder();
    data["hashes"] = torrent.Hashes();
    data["comment"] = torrent.comment;
    data["creator"] = torrent.creator;
    data["created"] = torrent.created;
    data["piece_size"] = metadata ? metadata->piece_length() : 0;
    data["private"] = metadata ? Json(metadata->priv()) : Json();
    lt::add_torrent_params magnet;
    magnet.ti = metadata;
    magnet.info_hashes = metadata ? metadata->info_hashes() : torrent.handle.info_hashes();
    magnet.name = name;
    magnet.trackers = Urls(torrent.handle.trackers());
    data["magnet"] = lt::make_magnet_uri(magnet);
    return data;
}

Json DescribeFiles(lt::torrent_handle const& handle,
    std::shared_ptr<lt::torrent_info const> const& metadata)
{
    Json data;
    data["files"] = Files(metadata);
    if (!metadata)
    {
        return data;
    }
    auto priorities = handle.get_file_priorities();
    auto progress = handle.file_progress();
    for (auto& file : data["files"])
    {
        auto index = file.at("index").get<size_t>();
        file["downloaded"] = index < progress.size() ? progress[index] : 0;
        if (index < priorities.size())
        {
            file["priority"] = static_cast<std::uint8_t>(priorities[index]);
        }
    }
    return data;
}

Json DescribePieces(lt::torrent_handle const& handle,
    std::shared_ptr<lt::torrent_info const> const& metadata, bool includeFiles)
{
    auto current = handle.status(lt::torrent_handle::query_pieces);
    auto count = metadata ? metadata->num_pieces() : 0;
    Json data;
    data["piece_size"] = metadata ? metadata->piece_length() : 0;
    data["peers"] = current.num_peers;
    data["verified"] = Json::array();
    for (int index = 0; index < count; ++index)
    {
        data["verified"].push_back(current.is_seeding ||
            (index < current.pieces.size() && current.pieces[lt::piece_index_t(index)]));
    }
    std::vector<int> availability;
    if (metadata)
    {
        handle.piece_availability(availability);
    }
    availability.resize(count, 0);
    data["availability"] = std::move(availability);
    data["downloading"] = Json::array();
    if (metadata)
    {
        // The borrowed block arrays stay valid until the next queue query.
        for (auto const& piece : handle.get_download_queue())
        {
            std::int64_t bytes = 0;
            for (int index = 0; index < piece.blocks_in_piece; ++index)
            {
                auto const& block = piece.blocks[index];
                bytes += block.state == lt::block_info::writing || block.state == lt::block_info::finished ?
                    block.block_size : block.bytes_progress;
            }
            data["downloading"].push_back({{"index", static_cast<int>(piece.piece_index)},
                {"progress", std::clamp(double(bytes) / metadata->piece_size(piece.piece_index), 0.0, 1.0)}});
        }
    }
    if (includeFiles)
    {
        data["files"] = Json::array();
        if (metadata)
        {
            auto const& files = metadata->layout();
            for (auto index : files.file_range())
            {
                if (files.pad_file_at(index))
                {
                    continue;
                }
                auto first = files.file_offset(index) / metadata->piece_length();
                auto end = files.file_size(index) == 0 ? first :
                    (files.file_offset(index) + files.file_size(index) + metadata->piece_length() - 1) /
                        metadata->piece_length();
                data["files"].push_back({{"path", files.file_path(index)},
                    {"first_piece", first}, {"end_piece", end}});
            }
        }
    }
    return data;
}
}

Json Torrent::Describe(TorrentView view, bool includeFiles) const
{
    auto metadata = handle.torrent_file();
    Json data = {{"torrent_id", identity}, {"metadata_ready", bool(metadata)}};
    switch (view)
    {
    case TorrentView::Peers:
        data.update(DescribePeers(handle));
        break;
    case TorrentView::Trackers:
        data.update(DescribeTrackers(handle));
        break;
    case TorrentView::General:
        data.update(DescribeGeneral(*this, metadata));
        break;
    case TorrentView::Files:
        data.update(DescribeFiles(handle, metadata));
        break;
    case TorrentView::Pieces:
        data.update(DescribePieces(handle, metadata, includeFiles));
        break;
    }
    return data;
}

Json Torrent::Row(bool allPaused) const
{
    auto problem = Diagnose();
    return {{"torrent_id", identity}, {"save_path", facts.savePath},
        {"moving", moving}, {"move_destination", facts.moveDestination},
        {"paused", facts.intent == Intent::Paused}, {"forced", facts.intent == Intent::Forced},
        {"added", facts.added}, {"name", Name()}, {"size", status.total_wanted},
        {"progress", status.progress}, {"status", ToString(Classify(allPaused))},
        {"download_rate", status.download_payload_rate}, {"upload_rate", status.upload_payload_rate},
        {"error", problem ? ToString(problem->kind) : ""}, {"detail", problem ? problem->detail : ""},
        {"seeds", status.num_seeds}, {"peers", status.num_peers},
        {"downloaded", status.all_time_download}, {"uploaded", status.all_time_upload},
        {"queue", static_cast<int>(status.queue_position)},
        {"complete", status.has_metadata && status.is_finished && !flushing},
        {"incoming", status.has_incoming}, {"hashes", Hashes()}};
}

void Torrent::ApplyIntent()
{
    if (!conflict.empty() || moving || !facts.moveDestination.empty())
    {
        handle.unset_flags(lt::torrent_flags::auto_managed);
        handle.pause();
        return;
    }
    auto metadata = handle.torrent_file();
    auto priorities = facts.priorities;
    if (priorities.empty() && metadata)
    {
        priorities = DefaultPriorities(metadata->layout());
    }
    handle.prioritize_files(priorities);
    if (facts.intent == Intent::Paused)
    {
        handle.unset_flags(lt::torrent_flags::auto_managed);
        handle.pause();
    }
    else
    {
        if (!metadata || facts.intent == Intent::Forced)
        {
            handle.unset_flags(lt::torrent_flags::auto_managed);
        }
        else
        {
            handle.set_flags(lt::torrent_flags::auto_managed);
        }
        handle.resume();
    }
    unsaved = true;
}

void Torrent::Checkpoint(bool exiting)
{
    checkpointPhase = CheckpointPhase::Requested;
    auto flags = lt::torrent_handle::save_info_dict;
    // After a failed save libtorrent has already cleared its change flags,
    // and Exit saves everything, so both save without checking for change.
    if (!exiting && !checkpointError)
    {
        flags |= dirty;
        if (status.all_time_upload != savedUploaded)
        {
            flags |= lt::torrent_handle::if_counters_changed;
        }
    }
    handle.save_resume_data(flags);
}
}
