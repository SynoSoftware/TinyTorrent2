#include "Torrent.h"
#include "Store.h"
#include <Windows.h>
#include <libtorrent/hex.hpp>
#include <libtorrent/magnet_uri.hpp>
#include <libtorrent/peer_info.hpp>
#include <libtorrent/torrent_info.hpp>
#include <algorithm>
#include <stdexcept>
#include <sstream>
#include <unordered_set>
#include <utility>

namespace tt
{
namespace
{
// The changes since the last checkpoint that make a new one worth writing.
constexpr lt::resume_data_flags_t dirty = lt::torrent_handle::if_download_progress |
    lt::torrent_handle::if_config_changed | lt::torrent_handle::if_state_changed |
    lt::torrent_handle::if_metadata_changed;

// libtorrent checks the torrent's files in this state.
bool Checks(lt::torrent_status::state_t state)
{
    return state == lt::torrent_status::checking_files || state == lt::torrent_status::checking_resume_data;
}

char const* ToString(Status status)
{
    switch (status)
    {
    case Status::Moving: return "moving";
    case Status::Error: return "error";
    case Status::Paused: return "paused";
    case Status::AllPaused: return "all_paused";
    case Status::Checking: return "checking";
    case Status::WaitingCheck: return "waiting_check";
    case Status::WaitingMove: return "waiting_move";
    case Status::Metadata: return "metadata";
    case Status::Queued: return "queued";
    case Status::Seeding: return "seeding";
    case Status::Completed: return "completed";
    case Status::Downloading: return "downloading";
    }
    return "";
}

// A file priority as the document and the add command write it.
bool IsPriority(Json const& value)
{
    return value.is_number_integer() && value >= 0 && value <= 255;
}
}

Problem Problem::From(Outcome const& failure, ProblemKind kind)
{
    return failure.error == ErrorCode::Overloaded ? Problem{ProblemKind::StorageOverloaded} : Problem{kind, failure.detail};
}

// ToOutcome reports a refused job as Overloaded; the error code it gives any
// other failure is not read.
Problem Problem::From(StorageOutcome const& failure, ProblemKind kind)
{
    return From(failure.ToOutcome(ErrorCode::StorageFailed), kind);
}

char const* Problem::Code() const
{
    return refusal ? ToString(*refusal) : ToString(kind);
}

char const* ToString(ProblemKind kind)
{
    switch (kind)
    {
    case ProblemKind::MoveInterrupted: return "move_interrupted";
    case ProblemKind::MoveUnfinished: return "move_unfinished";
    case ProblemKind::MoveFailed: return "move_failed";
    case ProblemKind::DestinationExists: return "destination_exists";
    case ProblemKind::SameLocation: return "same_location";
    case ProblemKind::AliasConflict: return "alias_conflict";
    case ProblemKind::TorrentError: return "torrent_error";
    case ProblemKind::StorageFailed: return "storage_failed";
    case ProblemKind::StorageOverloaded: return "storage_overloaded";
    case ProblemKind::CheckpointFailed: return "checkpoint_failed";
    case ProblemKind::DriveStalled: return "drive_stalled";
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

std::string ContentPath(lt::file_storage const& files, lt::file_index_t index, Layout layout)
{
    auto path = std::filesystem::path(Wide(files.file_path(index)));
    bool hasFolder = path.has_parent_path() && *path.begin() == std::filesystem::path(Wide(files.name()));
    if (layout == Layout::Strip && hasFolder)
        path = path.lexically_relative(*path.begin());
    else if (layout == Layout::Create && !hasFolder)
        path = std::filesystem::path(Wide(files.name())) / path;
    return Utf8(path.generic_wstring());
}

std::vector<lt::download_priority_t> DefaultPriorities(lt::file_storage const& files, std::string const& patterns)
{
    std::vector<std::wstring> masks;
    std::wistringstream lines{Wide(patterns)};
    for (std::wstring line; std::getline(lines, line);)
    {
        auto first = line.find_first_not_of(L" \t\r");
        if (first != std::wstring::npos)
            masks.push_back(line.substr(first, line.find_last_not_of(L" \t\r") - first + 1));
    }
    auto matches = [](std::wstring const& name, std::wstring const& mask)
    {
        std::size_t file = 0, pattern = 0, star = std::wstring::npos, retry = 0;
        while (file < name.size())
        {
            if (pattern < mask.size() && (mask[pattern] == L'?' ||
                CompareStringOrdinal(&name[file], 1, &mask[pattern], 1, TRUE) == CSTR_EQUAL))
            {
                ++file;
                ++pattern;
            }
            else if (pattern < mask.size() && mask[pattern] == L'*')
            {
                star = pattern++;
                retry = file;
            }
            else if (star != std::wstring::npos)
            {
                pattern = star + 1;
                file = ++retry;
            }
            else
                return false;
        }
        while (pattern < mask.size() && mask[pattern] == L'*')
            ++pattern;
        return pattern == mask.size();
    };
    std::vector<lt::download_priority_t> priorities;
    for (auto index : files.file_range())
    {
        auto name = std::filesystem::path(Wide(files.file_path(index))).filename().wstring();
        bool skipped = std::any_of(masks.begin(), masks.end(), [&](auto const& mask) { return matches(name, mask); });
        priorities.push_back(skipped ? lt::dont_download : DefaultPriority(files, index));
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

std::vector<std::filesystem::path> Torrent::Paths(bool logical) const
{
    auto metadata = restore ? restore->ti : names ? names->metadata : nullptr;
    if (logical && !restore)
        metadata = handle.torrent_file();
    if (!metadata)
    {
        return {};
    }
    if (logical)
    {
        std::vector<std::filesystem::path> paths;
        for (auto index : metadata->layout().file_range())
            if (!metadata->layout().pad_file_at(index))
                paths.push_back(Wide(ContentPath(metadata->layout(), index, facts.layout)));
        return paths;
    }
    lt::renamed_files restored;
    if (restore)
        restored.import_filenames(metadata->layout(), restore->renamed_files);
    lt::filenames files(metadata->layout(), restore ? restored : names->mappings);
    std::vector<std::filesystem::path> paths;
    for (auto index : files.file_range())
    {
        if (!metadata->layout().pad_file_at(index))
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
        {"final_folder", finalFolder},
        {"append_suffix", appendsSuffix},
        {"layout", static_cast<int>(layout)},
        {"skip_patterns", skipPatterns},
        {"move_destination", marker ? marker->destination : std::string()},
        {"move_stage", static_cast<int>(marker ? marker->stage : MoveStage::Staged)},
        {"move_copy", marker && marker->copy ? marker->copy->path : std::string()},
        {"move_copy_bytes", marker && marker->copy ? marker->copy->bytes : 0},
        {"move_found", marker ? marker->found : std::vector<std::string>{}},
        {"move_failed", marker && marker->failed},
        {"torrent_file", torrentFile},
        {"verify_files", verifyFiles},
        {"paused", intent == Intent::Paused},
        {"forced", intent == Intent::Forced},
        {"ignores_seed_limits", ignoresSeedLimits},
        {"added", added},
        {"priorities", std::move(chosen)},
        {"hashes", hashes},
        {"sequential", sequential},
        {"first_last", firstLast},
        {"download_limit", downloadLimit},
        {"upload_limit", uploadLimit}};
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

// A value the record holds incorrectly keeps its default, so one damaged value
// does not cost the torrent or the rest of the list.
Facts Facts::Read(Json const& saved)
{
    Facts facts;
    facts.savePath = ReadSaved(saved, "save_path", std::string());
    facts.finalFolder = ReadSaved(saved, "final_folder", std::string());
    facts.appendsSuffix = ReadSaved(saved, "append_suffix", true);
    auto layout = ReadSaved(saved, "layout", 0);
    if (layout >= 0 && layout <= static_cast<int>(Layout::Strip))
        facts.layout = static_cast<Layout>(layout);
    if (auto patterns = ReadSaved(saved, "skip_patterns", std::string()); patterns.size() <= patternLimit)
        facts.skipPatterns = std::move(patterns);
    if (auto destination = ReadSaved(saved, "move_destination", std::string()); !destination.empty())
    {
        facts.marker = Facts::Marker{std::move(destination)};
        // A move saved without a valid stage counts as unchecked. Earlier
        // versions saved 2 for a checked move that waited for the person.
        auto stage = ReadSaved(saved, "move_stage", 0);
        if (stage == 1 || stage == 2)
            facts.marker->stage = MoveStage::Placing;
        facts.marker->failed = stage == 2 || ReadSaved(saved, "move_failed", false);
        if (auto found = saved.find("move_found"); found != saved.end() && found->is_array())
        {
            for (auto const& path : *found)
            {
                if (path.is_string())
                    facts.marker->found.push_back(path.get<std::string>());
            }
        }
        if (auto copy = ReadSaved(saved, "move_copy", std::string()); !copy.empty())
            facts.marker->copy = Facts::Marker::Copy{std::move(copy), ReadSaved(saved, "move_copy_bytes",
                std::uint64_t(0))};
    }
    facts.torrentFile = ReadSaved(saved, "torrent_file", std::string());
    facts.verifyFiles = ReadSaved(saved, "verify_files", false);
    if (ReadSaved(saved, "paused", false))
    {
        facts.intent = Intent::Paused;
    }
    else if (ReadSaved(saved, "forced", false))
    {
        facts.intent = Intent::Forced;
    }
    facts.added = ReadSaved(saved, "added", std::int64_t{0});
    facts.ignoresSeedLimits = ReadSaved(saved, "ignores_seed_limits", false);
    if (auto found = saved.find("priorities"); found != saved.end() && found->is_array())
    {
        for (auto const& value : *found)
        {
            facts.priorities.push_back(IsPriority(value) ? lt::download_priority_t(value.get<std::uint8_t>()) :
                lt::default_priority);
        }
    }
    if (auto found = saved.find("hashes"); found != saved.end() && found->is_array())
    {
        for (auto const& value : *found)
        {
            if (value.is_string())
            {
                facts.hashes.push_back(value.get<std::string>());
            }
        }
    }
    facts.sequential = ReadSaved(saved, "sequential", false);
    facts.firstLast = ReadSaved(saved, "first_last", false);
    facts.downloadLimit = ReadSaved(saved, "download_limit", 0);
    facts.uploadLimit = ReadSaved(saved, "upload_limit", 0);
    if (auto found = saved.find("trackers"); found != saved.end() && found->is_array())
    {
        facts.trackers.emplace();
        for (auto const& entry : *found)
        {
            auto url = entry.is_string() ? entry.get<std::string>() : ReadSaved(entry, "url", std::string());
            if (url.empty())
            {
                continue;
            }
            lt::announce_entry tracker(url);
            tracker.tier = static_cast<std::uint8_t>(std::min(ReadSaved(entry, "tier", 0), 255));
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
        if (!IsPriority(value))
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
    auto metadata = restore ? restore->ti : handle.torrent_file();
    if (!metadata)
        return Utf8(folder.wstring());
    std::optional<std::filesystem::path> first;
    bool multiple = false;
    for (auto index : metadata->layout().file_range())
    {
        if (metadata->layout().pad_file_at(index))
            continue;
        auto path = std::filesystem::path(Wide(ContentPath(metadata->layout(), index, facts.layout)));
        if (!first)
        {
            first = std::move(path);
        }
        else
        {
            multiple = true;
            if (!first->has_parent_path() || *path.begin() != *first->begin())
                return Utf8(folder.wstring());
        }
    }
    if (first)
        folder = multiple ? folder / *first->begin() : (folder / *first).parent_path();
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
    if (checkError)
    {
        return checkError;
    }
    // A waiting move the person asked for replaces the interrupted one.
    if (!moving && !waitingMove && facts.marker)
    {
        return Problem{unfinished ? ProblemKind::MoveUnfinished : ProblemKind::MoveInterrupted,
            facts.marker->destination};
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

bool Torrent::MoveWaits() const
{
    return !moving && (waitingMove || unfinished == TaskKind::Move) && !Error();
}

bool Torrent::FilesBusy() const
{
    return moving || namePhase == NamePhase::Preparing || namePhase == NamePhase::Recovering ||
        !renaming.empty() || !marking.empty();
}

bool Torrent::IsHeld(bool hasMetadata) const
{
    return !conflict.empty() || moving || facts.marker || cacheWrite == CacheWrite::Flushing || CheckWaits() ||
        checkError || unfinished == TaskKind::Check || (hasMetadata && namePhase != NamePhase::Ready);
}

bool Torrent::CheckWaits() const
{
    return checkTurn && !checkTurn->running;
}

bool Torrent::NamesKnown() const
{
    return namePhase != NamePhase::Recovering && (restore || !handle.torrent_file() || names);
}

bool Torrent::ChecksFiles(bool sessionPaused) const
{
    return !sessionPaused && status.state == lt::torrent_status::checking_files &&
        !(status.flags & lt::torrent_flags::paused) && !moving && !CheckWaits();
}

bool Torrent::MoveBlocked() const
{
    return moveError && moveError->refusal != ErrorCode::MetadataUnavailable;
}

void Torrent::AnswerMoveProblem()
{
    if (!facts.marker)
    {
        moveError.reset();
    }
}

Status Torrent::Classify(bool sessionPaused) const
{
    if (moving)
    {
        return Status::Moving;
    }
    if (MoveWaits())
    {
        return Status::WaitingMove;
    }
    return ClassifyTransfer(sessionPaused);
}

Status Torrent::ClassifyTransfer(bool sessionPaused) const
{
    if (Error())
    {
        return Status::Error;
    }
    if (facts.intent == Intent::Paused)
    {
        return Status::Paused;
    }
    if (sessionPaused)
    {
        return Status::AllPaused;
    }
    if (completion.IsChecking() || Checks(status.state) || unfinished == TaskKind::Check)
    {
        return CheckWaits() || unfinished == TaskKind::Check ? Status::WaitingCheck : Status::Checking;
    }
    if (!status.has_metadata)
    {
        return Status::Metadata;
    }
    // A torrent the engine holds keeps the status its choice gives it, because
    // Queued would claim that a queue limit holds it.
    if (bool(status.flags & lt::torrent_flags::paused) && !IsHeld(status.has_metadata))
    {
        return Status::Queued;
    }
    if (completion.IsDownloading())
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
    completion.Sampled(status, latest);
    status = std::move(latest);
}

bool Completion::Changed(State state, State previous)
{
    origin_ = state;
    if (state == lt::torrent_status::downloading)
    {
        if (phase_ != CompletionPhase::Checking)
            phase_ = CompletionPhase::Downloading;
        received_ = false;
    }
    if (Checks(state))
    {
        received_ = false;
        if (phase_ == CompletionPhase::Downloading)
            phase_ = CompletionPhase::Idle;
    }
    if (state == lt::torrent_status::finished && previous == lt::torrent_status::downloading &&
        (phase_ == CompletionPhase::Downloading || phase_ == CompletionPhase::Idle || received_))
    {
        phase_ = CompletionPhase::Downloading;
        requested_ = true;
        return true;
    }
    return false;
}

void Completion::Sampled(lt::torrent_status const& previous, lt::torrent_status const& latest)
{
    received_ |= phase_ == CompletionPhase::Downloading &&
        latest.total_payload_download > previous.total_payload_download;
}

bool Completion::Observed(lt::torrent_status const& previous, lt::torrent_status const& latest)
{
    requested_ = false;
    bool checking = Checks(latest.state);
    if (phase_ == CompletionPhase::Checking && !checking)
        phase_ = latest.is_finished ? CompletionPhase::Checked : CompletionPhase::Idle;
    if (latest.state == lt::torrent_status::downloading)
        phase_ = CompletionPhase::Downloading;
    // Restarting resets the counter. This fresh read can recognize a download
    // that finishes between samples; a routine status cannot establish a reset.
    received_ |= phase_ == CompletionPhase::Downloading && latest.total_payload_download > 0 &&
        latest.total_payload_download != previous.total_payload_download;
    if (checking)
    {
        received_ = false;
        if (phase_ == CompletionPhase::Downloading)
            phase_ = CompletionPhase::Idle;
        return false;
    }
    if (latest.is_finished && phase_ == CompletionPhase::Downloading)
        phase_ = std::exchange(received_, false) ? CompletionPhase::Flushing : CompletionPhase::Idle;
    return phase_ == CompletionPhase::Flushing;
}

bool Completion::FileFinished()
{
    if (origin_ != lt::torrent_status::downloading)
        return false;
    received_ = true;
    return true;
}

void Completion::Checked()
{
    if (phase_ == CompletionPhase::Checking)
        requested_ = true;
}

bool Completion::Flushed()
{
    if (phase_ != CompletionPhase::Flushing)
        return false;
    phase_ = CompletionPhase::Settling;
    return true;
}

void Completion::Rechecked()
{
    received_ = false;
}

void Completion::Lost(bool stateChanges)
{
    requested_ = true;
    if (stateChanges)
        origin_.reset();
}

bool Completion::BeginCheck()
{
    if (phase_ != CompletionPhase::Settling)
        return false;
    phase_ = CompletionPhase::Checking;
    return true;
}

void Completion::FailCheck()
{
    if (phase_ == CompletionPhase::Checking)
        phase_ = CompletionPhase::Settling;
}

bool Completion::Finish()
{
    if (phase_ != CompletionPhase::Settling && phase_ != CompletionPhase::Checked)
        return false;
    phase_ = CompletionPhase::Idle;
    return true;
}

bool Completion::IsReached(lt::torrent_status const& status) const
{
    return status.has_metadata && status.is_finished && !IsWorking();
}

bool Completion::IsWorking() const
{
    return IsDownloading() || phase_ == CompletionPhase::Checking;
}

bool Completion::IsDownloading() const
{
    return phase_ == CompletionPhase::Downloading || phase_ == CompletionPhase::Flushing;
}

std::vector<lt::announce_entry> const& Torrent::Trackers() const
{
    return facts.trackers ? *facts.trackers : sourceTrackers;
}

Json Torrent::Describe(Detail const& detail, std::shared_ptr<lt::torrent_info const> const& metadata) const
{
    auto data = facts.ToJson();
    data.update(Describe(TorrentView::General, false, detail, metadata));
    data.update(Describe(TorrentView::Files, false, detail, metadata));
    if (facts.trackers)
    {
        data["trackers"] = Urls(*facts.trackers);
    }
    return data;
}

namespace
{
char const* ToString(PeerState state)
{
    switch (state)
    {
    case PeerState::Connecting: return "connecting";
    case PeerState::Handshake: return "handshake";
    case PeerState::Connected: return "connected";
    }
    return "";
}

char const* ToString(PeerEncryption encryption)
{
    switch (encryption)
    {
    case PeerEncryption::Unknown: return "unknown";
    case PeerEncryption::Plain: return "plain";
    case PeerEncryption::Handshake: return "handshake";
    case PeerEncryption::Rc4: return "rc4";
    case PeerEncryption::Tls: return "tls";
    }
    return "";
}

char const* ToString(TrackerStatus status)
{
    switch (status)
    {
    case TrackerStatus::Disabled: return "disabled";
    case TrackerStatus::Announcing: return "announcing";
    case TrackerStatus::Working: return "working";
    case TrackerStatus::Error: return "error";
    case TrackerStatus::Waiting: return "waiting";
    }
    return "";
}

std::string DescribeEndpoint(lt::tcp::endpoint const& endpoint)
{
    auto address = endpoint.address().to_string();
    return (endpoint.address().is_v6() ? "[" + address + "]" : address) +
        ":" + std::to_string(endpoint.port());
}

Json DescribeWait(lt::bandwidth_state_flags_t state)
{
    auto waits = Json::array();
    if (bool(state & lt::peer_info::bw_limit)) waits.push_back("limit");
    if (bool(state & lt::peer_info::bw_network)) waits.push_back("network");
    if (bool(state & lt::peer_info::bw_disk)) waits.push_back("disk");
    return waits;
}

Json DescribeSources(lt::peer_source_flags_t source)
{
    auto sources = Json::array();
    if (bool(source & lt::peer_info::tracker)) sources.push_back("tracker");
    if (bool(source & lt::peer_info::dht)) sources.push_back("dht");
    if (bool(source & lt::peer_info::pex)) sources.push_back("pex");
    if (bool(source & lt::peer_info::lsd)) sources.push_back("lsd");
    if (bool(source & lt::peer_info::resume_data)) sources.push_back("resume");
    if (bool(source & lt::peer_info::incoming)) sources.push_back("incoming");
    return sources;
}

Json DescribePeers(Detail const& detail)
{
    Json data;
    data["peers"] = Json::array();
    std::unordered_set<std::string> endpoints;
    for (auto const& peer : detail.peers)
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
            address = DescribeEndpoint(peer.remote_endpoint());
        }
        auto webSeed = bool(peer.connection_type & (lt::peer_info::web_seed | lt::peer_info::http_seed));
        if (webSeed)
        {
            transport = "http";
        }
        auto state = PeerState::Connected;
        if (bool(peer.flags & lt::peer_info::connecting))
        {
            state = PeerState::Connecting;
        }
        else if (!webSeed && bool(peer.flags & lt::peer_info::handshake))
        {
            state = PeerState::Handshake;
        }
        auto encryption = PeerEncryption::Unknown;
        if (!webSeed && state == PeerState::Connected)
        {
            if (bool(peer.flags & lt::peer_info::ssl_socket))
                encryption = PeerEncryption::Tls;
            else if (bool(peer.flags & lt::peer_info::rc4_encrypted))
                encryption = PeerEncryption::Rc4;
            else if (bool(peer.flags & lt::peer_info::plaintext_encrypted))
                encryption = PeerEncryption::Handshake;
            else
                encryption = PeerEncryption::Plain;
        }
        endpoints.insert(address);
        data["peers"].push_back({{"endpoint", address}, {"client", peer.client},
            {"role", bool(peer.flags & lt::peer_info::seed) ? "seed" :
                state == PeerState::Connected && !webSeed ? "leecher" : "unknown"},
            {"transport", transport},
            {"incoming", !bool(peer.flags & lt::peer_info::outgoing_connection)},
            {"state", ToString(state)}, {"encryption", ToString(encryption)},
            {"interested", bool(peer.flags & lt::peer_info::interesting)},
            {"choked", bool(peer.flags & lt::peer_info::choked)},
            {"remote_interested", bool(peer.flags & lt::peer_info::remote_interested)},
            {"remote_choked", bool(peer.flags & lt::peer_info::remote_choked)},
            {"read_wait", DescribeWait(peer.read_state)}, {"write_wait", DescribeWait(peer.write_state)},
            {"sources", DescribeSources(peer.source)},
            {"progress", peer.progress_ppm / 1'000'000.0},
            {"download_rate", peer.payload_down_speed}, {"upload_rate", peer.payload_up_speed},
            {"downloaded", peer.total_download}, {"uploaded", peer.total_upload}});
    }
    for (auto const& peer : detail.knownPeers)
    {
        auto address = DescribeEndpoint(peer.endpoint);
        if (endpoints.contains(address)) continue;
        data["peers"].push_back({{"endpoint", std::move(address)}, {"state", "known"},
            {"role", peer.seed ? "seed" : "unknown"}, {"sources", DescribeSources(peer.source)}});
    }
    return data;
}

TrackerStatus ClassifyTracker(lt::announce_infohash const& state, bool usable)
{
    if (!usable) return TrackerStatus::Disabled;
    if (state.updating) return TrackerStatus::Announcing;
    if (state.last_error || state.fails > 0) return TrackerStatus::Error;
    return state.start_sent ? TrackerStatus::Working : TrackerStatus::Waiting;
}

TrackerStatus PreferStatus(TrackerStatus current, TrackerStatus candidate)
{
    for (auto status : {TrackerStatus::Announcing, TrackerStatus::Working,
        TrackerStatus::Error, TrackerStatus::Waiting, TrackerStatus::Disabled})
    {
        if (status == current || status == candidate) return status;
    }
    return TrackerStatus::Disabled;
}

// One tracker's row. `now` and `wall` are the same moment on libtorrent's
// clock and the system clock, read once for the whole list.
Json DescribeTracker(lt::announce_entry const& tracker, lt::info_hash_t const& hashes,
    lt::time_point now, std::time_t wall)
{
    auto summary = tracker.endpoints.empty() ? TrackerStatus::Waiting : TrackerStatus::Disabled;
    std::string message;
    int seedCount = -1;
    int leecherCount = -1;
    int downloadCount = -1;
    std::int64_t next = 0;
    auto endpoints = Json::array();
    for (auto const& endpoint : tracker.endpoints)
    {
        for (auto version : {lt::protocol_version::V1, lt::protocol_version::V2})
        {
            if (!hashes.has(version))
            {
                continue;
            }
            auto const& state = endpoint.info_hashes[version];
            auto usable = endpoint.enabled && (tracker.fail_limit == 0 || state.fails < tracker.fail_limit);
            auto status = ClassifyTracker(state, usable);
            auto diagnostic = state.message;
            if (diagnostic.empty() && state.last_error)
            {
                diagnostic = state.last_error.message();
            }
            auto preferred = PreferStatus(summary, status);
            if (preferred != summary)
            {
                summary = preferred;
                message.clear();
            }
            if (status == summary && !diagnostic.empty())
            {
                message = diagnostic;
            }
            endpoints.push_back({{"address", DescribeEndpoint(endpoint.local_endpoint)},
                {"version", version == lt::protocol_version::V1 ? "v1" : "v2"},
                {"status", ToString(status)}, {"failures", static_cast<int>(state.fails)},
                {"message", std::move(diagnostic)}});
            if (!endpoint.enabled)
            {
                continue;
            }
            seedCount = std::max(seedCount, state.scrape_complete);
            leecherCount = std::max(leecherCount, state.scrape_incomplete);
            downloadCount = std::max(downloadCount, state.scrape_downloaded);
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
        }
    }
    return {{"url", tracker.url}, {"tier", tracker.tier}, {"status", ToString(summary)}, {"seed_count", seedCount},
        {"leecher_count", leecherCount}, {"download_count", downloadCount}, {"next_announce", next},
        {"message", message}, {"endpoints", std::move(endpoints)}};
}

Json DescribeTrackers(std::vector<lt::announce_entry> const& trackers, lt::info_hash_t const& hashes)
{
    Json data;
    data["trackers"] = Json::array();
    auto now = lt::clock_type::now();
    auto wall = std::chrono::system_clock::to_time_t(std::chrono::system_clock::now());
    for (auto const& tracker : trackers)
    {
        data["trackers"].push_back(DescribeTracker(tracker, hashes, now, wall));
    }
    return data;
}

Json DescribeGeneral(Torrent const& torrent, std::shared_ptr<lt::torrent_info const> const& metadata,
    std::vector<lt::announce_entry> const& trackers)
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
    data["piece_count"] = metadata ? metadata->num_pieces() : 0;
    data["private"] = metadata ? Json(metadata->priv()) : Json();
    lt::add_torrent_params magnet;
    magnet.ti = metadata;
    magnet.info_hashes = metadata ? metadata->info_hashes() : torrent.handle.info_hashes();
    magnet.name = name;
    magnet.trackers = Urls(trackers);
    data["magnet"] = lt::make_magnet_uri(magnet);
    return data;
}

Json DescribeFiles(Torrent const& torrent, std::shared_ptr<lt::torrent_info const> const& metadata, Detail const& detail,
    std::chrono::steady_clock::time_point since)
{
    Json data;
    data["files"] = Files(metadata);
    if (!metadata)
    {
        return data;
    }
    for (auto& file : data["files"])
    {
        auto index = file.at("index").get<size_t>();
        file["disk_path"] = detail.Has(DetailKind::Status, since) && torrent.names ?
            Json(torrent.names->mappings.file_path(metadata->layout(),
                lt::file_index_t(static_cast<int>(index)))) : Json();
        file["downloaded"] = detail.Has(DetailKind::Progress, since) && index < detail.progress.size() ?
            Json(detail.progress[index]) : Json();
        // Choices that wait for busy files are saved, so they show before
        // libtorrent has them.
        auto const& priorities = torrent.prioritiesPending ? torrent.facts.priorities : detail.priorities;
        file["priority"] = (torrent.prioritiesPending || detail.Has(DetailKind::Priorities, since)) &&
            index < priorities.size() ? Json(static_cast<std::uint8_t>(priorities[index])) : Json();
    }
    return data;
}

Json DescribePieces(Torrent const& torrent, std::shared_ptr<lt::torrent_info const> const& metadata,
    Detail const& detail, bool includeFiles)
{
    auto const& current = detail.status;
    auto count = metadata ? metadata->num_pieces() : 0;
    Json data;
    data["piece_size"] = metadata ? metadata->piece_length() : 0;
    data["peer_count"] = torrent.status.num_peers;
    data["verified"] = Json::array();
    std::vector<bool> wanted(count, false);
    if (metadata)
    {
        auto const& files = metadata->layout();
        auto priorities = torrent.ChosenPriorities(metadata);
        for (auto index : files.file_range())
        {
            if (files.pad_file_at(index) || files.file_size(index) == 0 ||
                priorities[static_cast<int>(index)] == lt::dont_download)
                continue;
            auto first = files.file_offset(index) / metadata->piece_length();
            auto end = (files.file_offset(index) + files.file_size(index) + metadata->piece_length() - 1) /
                metadata->piece_length();
            std::fill(wanted.begin() + first, wanted.begin() + end, true);
        }
    }
    data["wanted"] = std::move(wanted);
    for (int index = 0; index < count; ++index)
    {
        data["verified"].push_back(current.is_seeding ||
            (index < current.pieces.size() && current.pieces[lt::piece_index_t(index)]));
    }
    auto availability = detail.availability;
    availability.resize(count, 0);
    data["availability"] = std::move(availability);
    data["downloading"] = Json::array();
    if (metadata)
    {
        for (auto const& [index, bytes] : detail.downloading)
        {
            data["downloading"].push_back({{"index", static_cast<int>(index)},
                {"progress", std::clamp(double(bytes) / metadata->piece_size(index), 0.0, 1.0)}});
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

Json Torrent::Describe(TorrentView view, bool includeFiles, Detail const& detail,
    std::shared_ptr<lt::torrent_info const> const& metadata,
    std::chrono::steady_clock::time_point since) const
{
    Json data = {{"torrent_id", torrentId}, {"metadata_ready", bool(metadata)}};
    switch (view)
    {
    case TorrentView::Peers:
        if (detail.Has(DetailKind::Peers, since))
            data.update(DescribePeers(detail));
        else
            data["peers"] = nullptr;
        break;
    case TorrentView::Trackers:
        if (detail.Has(DetailKind::Trackers, since))
            data.update(DescribeTrackers(detail.trackers, handle.info_hashes()));
        else
            data["trackers"] = nullptr;
        break;
    case TorrentView::General:
        data.update(DescribeGeneral(*this, metadata, detail.trackers));
        if (!detail.Has(DetailKind::Trackers, since))
            data["magnet"] = nullptr;
        break;
    case TorrentView::Files:
        data.update(DescribeFiles(*this, metadata, detail, since));
        break;
    case TorrentView::Pieces:
        if (!metadata || (detail.Has(DetailKind::Status, since) &&
            detail.Has(DetailKind::Availability, since) &&
            detail.Has(DetailKind::Downloading, since)))
            data.update(DescribePieces(*this, metadata, detail, includeFiles));
        break;
    }
    return data;
}

Json Torrent::Row(bool sessionPaused, int queue, std::optional<double> moveProgress, DriveWait const& wait) const
{
    auto problem = Diagnose();
    auto complete = completion.IsReached(status);
    return {{"torrent_id", torrentId}, {"save_path", facts.savePath}, {"final_folder", facts.finalFolder},
        {"moving", moving}, {"move_destination", facts.marker ? facts.marker->destination : std::string()},
        {"paused", facts.intent == Intent::Paused}, {"forced", facts.intent == Intent::Forced},
        {"sequential", facts.sequential}, {"first_last", facts.firstLast},
        {"download_limit", facts.downloadLimit}, {"upload_limit", facts.uploadLimit},
        {"added", facts.added}, {"name", Name()}, {"size", status.total_wanted},
        {"completed", status.total_wanted_done},
        {"progress", moveProgress.value_or(status.progress)}, {"status", ToString(Classify(sessionPaused))},
        {"waiting_drive", wait.drive}, {"waiting_torrent_id", wait.holderId},
        {"waiting_files", wait.files},
        {"download_rate", status.download_payload_rate}, {"upload_rate", status.upload_payload_rate},
        {"error", !problem ? "" : problem->Code()},
        {"detail", problem ? problem->detail : ""},
        {"seed_count", status.num_seeds}, {"peer_count", status.num_peers},
        {"known_peer_count", status.list_peers},
        // The tracker's scrape counts the whole swarm; without one, the peers this session has heard of
        // are the best estimate.
        {"swarm_seed_count", status.num_complete >= 0 ? status.num_complete : status.list_seeds},
        {"swarm_leecher_count", status.num_incomplete >= 0 ? status.num_incomplete : status.list_peers - status.list_seeds},
        {"downloaded", status.all_time_download}, {"uploaded", status.all_time_upload},
        {"queue", queue},
        {"complete", complete}, {"completed_on", complete ? status.completed_time : 0},
        {"incoming", status.has_incoming}, {"hashes", Hashes()}};
}

std::vector<lt::download_priority_t> Torrent::ChosenPriorities(
    std::shared_ptr<lt::torrent_info const> const& metadata) const
{
    auto priorities = facts.priorities;
    if (!metadata)
        std::fill(priorities.begin(), priorities.end(), lt::dont_download);
    if (priorities.empty() && metadata)
        priorities = DefaultPriorities(metadata->layout(), facts.skipPatterns);
    return priorities;
}

void Torrent::ApplyIntent()
{
    if (restore)
    {
        return;
    }
    handle.set_flags(facts.sequential ? lt::torrent_flags::sequential_download : lt::torrent_flags_t{},
        lt::torrent_flags::sequential_download);
    // The saved limits also replace those a resume file restored.
    handle.set_download_limit(facts.downloadLimit);
    handle.set_upload_limit(facts.uploadLimit);
    auto metadata = handle.torrent_file();
    if (IsHeld(bool(metadata)))
    {
        handle.unset_flags(lt::torrent_flags::auto_managed);
        handle.pause();
        return;
    }
    auto priorities = ChosenPriorities(metadata);
    handle.prioritize_files(priorities);
    if (facts.intent == Intent::Paused || (metadata && facts.priorities.empty() &&
        std::none_of(priorities.begin(), priorities.end(),
        [](auto priority) { return priority != lt::dont_download; })))
    {
        handle.unset_flags(lt::torrent_flags::auto_managed);
        handle.pause();
        // A stopped torrent keeps no peers, in memory or in its resume file.
        handle.clear_peers();
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

// Each piece gets the highest priority of the wanted files it holds, as
// libtorrent gives it. With firstLast the end pieces of each wanted file get
// the top priority: 1 % of the file at each end, at least one
// piece, which covers a media header and an AVI index.
void Torrent::PrioritizePieces(std::vector<lt::download_priority_t> const& priorities) const
{
    if (restore)
    {
        return;
    }
    auto metadata = handle.torrent_file();
    if (!metadata)
    {
        return;
    }
    auto const& files = metadata->layout();
    std::int64_t length = files.piece_length();
    std::vector<lt::download_priority_t> pieces(metadata->num_pieces(), lt::dont_download);
    for (auto index : files.file_range())
    {
        auto size = files.file_size(index);
        auto priority = priorities[static_cast<int>(index)];
        if (size == 0 || files.pad_file_at(index) || priority == lt::dont_download)
        {
            continue;
        }
        auto first = static_cast<int>(files.file_offset(index) / length);
        auto last = static_cast<int>((files.file_offset(index) + size - 1) / length);
        for (auto piece = first; piece <= last; ++piece)
        {
            pieces[piece] = std::max(pieces[piece], priority);
        }
        if (!facts.firstLast)
        {
            continue;
        }
        auto count = static_cast<int>((size + 100 * length - 1) / (100 * length));
        for (auto step = 0; step < count && first + step <= last; ++step)
        {
            pieces[first + step] = lt::top_priority;
            pieces[last - step] = lt::top_priority;
        }
    }
    handle.prioritize_pieces(pieces);
}

void Torrent::Checkpoint(bool force)
{
    if (restore)
    {
        unsaved = false;
        return;
    }
    checkpointPhase = CheckpointPhase::Requested;
    auto flags = lt::torrent_handle::save_info_dict;
    // Failed saves and discarded snapshots have already cleared the change
    // flags; a forced checkpoint must still produce fresh resume data.
    if (!force && !checkpointError)
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
