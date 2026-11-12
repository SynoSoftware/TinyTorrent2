#include "Torrent.h"
#include "Store.h"
#include "Tree.h"
#include <Windows.h>
#include <libtorrent/hex.hpp>
#include <libtorrent/magnet_uri.hpp>
#include <libtorrent/peer_info.hpp>
#include <libtorrent/torrent_info.hpp>
#include <algorithm>
#include <charconv>
#include <stdexcept>
#include <sstream>
#include <utility>

namespace tt
{
namespace
{
// The changes since the last checkpoint that make a new one worth writing.
constexpr lt::resume_data_flags_t dirty = lt::torrent_handle::if_download_progress |
    lt::torrent_handle::if_config_changed | lt::torrent_handle::if_state_changed |
    lt::torrent_handle::if_metadata_changed;

// The priorities a person can choose for a file, which IsChoice and
// NearestChoice share.
constexpr lt::download_priority_t choices[] = {lt::dont_download, lt::low_priority, lt::default_priority,
    lt::top_priority};

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
    case Status::FilesMissing: return "files_missing";
    case Status::Downloading: return "downloading";
    }
    return "";
}
}

bool Checks(lt::torrent_status::state_t state)
{
    return state == lt::torrent_status::checking_files || state == lt::torrent_status::checking_resume_data;
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

Problem Problem::From(lt::error_code const& error)
{
    if (error == boost::system::errc::no_such_file_or_directory)
        return {ProblemKind::DownloadPathMissing};
    if (error == boost::system::errc::no_space_on_device)
        return {ProblemKind::DiskFull};
    if (error == boost::system::errc::network_down || error == boost::system::errc::network_unreachable ||
        error == boost::system::errc::host_unreachable || error == boost::system::errc::not_connected)
        return {ProblemKind::DownloadPathUnavailable};
    if (error.category() == lt::system_category())
    {
        switch (error.value())
        {
        case ERROR_INVALID_NAME:
        case ERROR_BAD_PATHNAME:
        case ERROR_DIRECTORY:
        case ERROR_FILENAME_EXCED_RANGE:
            return {ProblemKind::DownloadPathInvalid};
        case ERROR_DISK_FULL:
        case ERROR_HANDLE_DISK_FULL:
            return {ProblemKind::DiskFull};
        case ERROR_NOT_READY:
        case ERROR_BAD_NETPATH:
        case ERROR_NETWORK_BUSY:
        case ERROR_DEV_NOT_EXIST:
        case ERROR_BAD_NET_RESP:
        case ERROR_UNEXP_NET_ERR:
        case ERROR_NETNAME_DELETED:
        case ERROR_NETWORK_ACCESS_DENIED:
        case ERROR_BAD_NET_NAME:
        case ERROR_CONNECTION_UNAVAIL:
        case ERROR_NO_NET_OR_BAD_PATH:
        case ERROR_NO_NETWORK:
        case ERROR_REM_NOT_LIST:
        case ERROR_NETWORK_UNREACHABLE:
        case ERROR_HOST_UNREACHABLE:
        case ERROR_NOT_CONNECTED:
        case ERROR_INVALID_DRIVE:
        case ERROR_DEVICE_NOT_CONNECTED:
        case ERROR_VC_DISCONNECTED:
        case ERROR_FILE_INVALID:
        case ERROR_REQUEST_ABORTED:
            return {ProblemKind::DownloadPathUnavailable};
        }
    }
    return {ProblemKind::TorrentError, error.message()};
}

bool Problem::IsStorage() const
{
    return kind == ProblemKind::TorrentError || kind == ProblemKind::DownloadPathMissing ||
        kind == ProblemKind::DownloadPathInvalid || kind == ProblemKind::DownloadPathUnavailable ||
        kind == ProblemKind::DiskFull || kind == ProblemKind::DriveStalled;
}

char const* ToString(ProblemKind kind)
{
    switch (kind)
    {
    case ProblemKind::MoveInterrupted: return "move_interrupted";
    case ProblemKind::MoveUnfinished: return "move_unfinished";
    case ProblemKind::MoveFailed: return "move_failed";
    case ProblemKind::MoveSourceUnavailable: return "move_source_unavailable";
    case ProblemKind::MoveDestinationUnavailable: return "move_destination_unavailable";
    case ProblemKind::DestinationExists: return "destination_exists";
    case ProblemKind::SameLocation: return "same_location";
    case ProblemKind::AliasConflict: return "alias_conflict";
    case ProblemKind::TorrentError: return "torrent_error";
    case ProblemKind::DownloadPathMissing: return "download_path_missing";
    case ProblemKind::DownloadPathInvalid: return "download_path_invalid";
    case ProblemKind::DownloadPathUnavailable: return "download_path_unavailable";
    case ProblemKind::DiskFull: return "disk_full";
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

std::vector<std::wstring> SkipMasks(std::string const& patterns)
{
    std::vector<std::wstring> masks;
    std::wistringstream lines{Wide(patterns)};
    for (std::wstring line; std::getline(lines, line);)
    {
        auto first = line.find_first_not_of(L" \t\r");
        if (first != std::wstring::npos)
            masks.push_back(line.substr(first, line.find_last_not_of(L" \t\r") - first + 1));
    }
    return masks;
}

// Padding files only align pieces, so they never download.
lt::download_priority_t DefaultPriority(lt::file_storage const& files, lt::file_index_t index,
    std::vector<std::wstring> const& masks)
{
    if (files.pad_file_at(index))
        return lt::dont_download;
    if (masks.empty())
        return lt::default_priority;
    auto matches = [](std::wstring const& name, std::wstring const& mask)
    {
        std::size_t file = 0, pattern = 0, star = std::wstring::npos, retry = 0;
        while (file < name.size())
        {
            if (pattern < mask.size() && (mask[pattern] == L'?' ||
                SameName({&name[file], 1}, {&mask[pattern], 1})))
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
    auto name = Wide(std::string(files.file_name(index)));
    bool skipped = std::any_of(masks.begin(), masks.end(), [&](auto const& mask) { return matches(name, mask); });
    return skipped ? lt::dont_download : lt::default_priority;
}

// The file browser builds this path for every file of a torrent, so it stays
// in UTF-8 rather than converting each one through std::filesystem::path.
std::string ContentPath(lt::file_storage const& files, lt::file_index_t index, Layout layout)
{
    auto path = files.file_path(index);
    std::replace(path.begin(), path.end(), '\\', '/');
    auto const& name = files.name();
    bool hasFolder = path.size() > name.size() && path[name.size()] == '/' && path.starts_with(name);
    if (layout == Layout::Strip && hasFolder)
        path.erase(0, name.size() + 1);
    else if (layout == Layout::Create && !hasFolder)
        path = name + '/' + path;
    return path;
}

std::vector<lt::download_priority_t> DefaultPriorities(lt::file_storage const& files, std::string const& patterns)
{
    auto masks = SkipMasks(patterns);
    std::vector<lt::download_priority_t> priorities;
    for (auto index : files.file_range())
        priorities.push_back(DefaultPriority(files, index, masks));
    return priorities;
}

bool IsChoice(lt::download_priority_t priority)
{
    return std::ranges::find(choices, priority) != std::end(choices);
}

lt::download_priority_t NearestChoice(std::uint8_t saved)
{
    auto distance = [saved](lt::download_priority_t choice)
    {
        auto value = static_cast<std::uint8_t>(choice);
        return value > saved ? value - saved : saved - value;
    };
    return std::ranges::min(choices, {}, distance);
}

std::vector<std::string> Tops(std::function<void(std::function<void(std::string)> const&)> const& visitPaths)
{
    auto separator = [](char character) { return character == '/' || character == '\\'; };
    std::vector<std::string> tops;
    visitPaths([&tops, &separator](std::string top)
    {
        bool full = top.size() > 1 && (top[1] == ':' || (separator(top[0]) && separator(top[1])));
        if (!full)
            top.resize(std::min(top.size(), top.find_first_of("/\\")));
        // Files of one folder follow each other, so most repeats end here.
        if (tops.empty() || tops.back() != top)
            tops.push_back(std::move(top));
    });
    std::sort(tops.begin(), tops.end());
    tops.erase(std::unique(tops.begin(), tops.end()), tops.end());
    return tops;
}

PieceRange Pieces(lt::torrent_info const& metadata, lt::file_index_t index)
{
    auto const& storage = metadata.layout();
    std::int64_t const length = metadata.piece_length();
    auto start = storage.file_offset(index);
    auto size = storage.file_size(index);
    return {start / length, size == 0 ? start / length : (start + size + length - 1) / length};
}

bool Complete(lt::torrent_info const& metadata, lt::file_index_t index,
    lt::typed_bitfield<lt::piece_index_t> const& pieces)
{
    auto range = Pieces(metadata, index);
    if (range.first == range.end || range.end > pieces.size())
        return false;
    for (auto piece = range.first; piece < range.end; ++piece)
    {
        if (!pieces.get_bit(lt::piece_index_t(static_cast<int>(piece))))
            return false;
    }
    return true;
}


void Torrent::VisitPaths(bool logical, std::function<void(std::string)> const& visit) const
{
    auto metadata = waiting ? waiting->params.ti : names ? names->metadata : nullptr;
    if (logical && !waiting)
        metadata = handle.torrent_file();
    if (!metadata)
    {
        return;
    }
    auto const& layout = metadata->layout();
    if (logical)
    {
        VisitContent(layout, facts.layout, [&visit](lt::file_index_t, std::string path) { visit(std::move(path)); });
        return;
    }
    lt::renamed_files restored;
    if (waiting)
        restored.import_filenames(layout, waiting->params.renamed_files);
    lt::filenames files(layout, waiting ? restored : names->mappings);
    for (auto index : files.file_range())
    {
        if (!layout.pad_file_at(index))
        {
            visit(files.file_path(index));
        }
    }
}

std::vector<std::filesystem::path> Torrent::Paths(bool logical) const
{
    std::vector<std::filesystem::path> paths;
    VisitPaths(logical, [&paths](std::string path) { paths.push_back(Wide(path)); });
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

void Facts::Revise(Change const& change)
{
    if (auto const* lost = std::get_if<Lost>(&change))
    {
        bool joined = false;
        for (auto file : lost->files)
        {
            auto item = static_cast<std::size_t>(static_cast<int>(file));
            // A lost file that the person skipped stays as it is: nothing
            // would download it.
            if (item >= lost->chosen.size() || lost->chosen[item] == lt::dont_download ||
                question_.files.contains(file))
                continue;
            if (priorities_.empty())
                priorities_ = lost->chosen;
            if (item >= priorities_.size())
                continue;
            question_.files.emplace(file, priorities_[item]);
            priorities_[item] = lt::dont_download;
            joined = true;
        }
        if (joined)
        {
            ++question_.number;
            repairs_ = repairs_ || lost->finished;
        }
    }
    else if (auto const* chosen = std::get_if<Chosen>(&change))
    {
        auto previous = std::exchange(priorities_, chosen->priorities);
        bool left = false;
        for (auto entry = question_.files.begin(); entry != question_.files.end();)
        {
            auto item = static_cast<std::size_t>(static_cast<int>(entry->first));
            if (chosen->named.contains(entry->first) || item >= priorities_.size())
            {
                entry = question_.files.erase(entry);
                left = true;
                continue;
            }
            priorities_[item] = lt::dont_download;
            ++entry;
        }
        if (left)
            ++question_.number;
        // A file that the person makes wanted downloads, and its finish is
        // no repair.
        for (std::size_t item = 0; item < priorities_.size() && item < previous.size(); ++item)
        {
            if (previous[item] == lt::dont_download && priorities_[item] != lt::dont_download)
                repairs_ = false;
        }
    }
    else if (auto const* found = std::get_if<Found>(&change))
    {
        bool left = false;
        for (auto file : found->files)
        {
            auto entry = question_.files.find(file);
            auto item = static_cast<std::size_t>(static_cast<int>(file));
            if (entry == question_.files.end())
                continue;
            if (item < priorities_.size())
                priorities_[item] = entry->second;
            question_.files.erase(entry);
            left = true;
        }
        if (left)
            ++question_.number;
    }
    else
    {
        repairs_ = false;
    }
}

Json Facts::ToJson() const
{
    Json chosen = Json::array();
    for (auto priority : priorities_)
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
        {"move_stage", !marker ? 0 : marker->stage == MoveStage::Returning ? 2 : static_cast<int>(marker->stage)},
        {"move_returns", marker && marker->stage == MoveStage::Returning},
        {"move_copy", marker && marker->copy ? marker->copy->path : std::string()},
        {"move_copy_bytes", marker && marker->copy ? marker->copy->bytes : 0},
        {"move_found", marker ? marker->found : std::vector<std::string>{}},
        {"move_failed", marker && marker->failed},
        {"move_download", marker && marker->mode == MoveMode::Download},
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
        {"upload_limit", uploadLimit},
        {"repairs", repairs_}};
    if (question_.number != 0)
    {
        Json files = Json::array();
        for (auto const& [file, priority] : question_.files)
        {
            files.push_back({{"index", static_cast<int>(file)}, {"priority", static_cast<std::uint8_t>(priority)}});
        }
        saved["question"] = {{"number", question_.number}, {"files", std::move(files)}};
    }
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
        facts.marker->mode = ReadSaved(saved, "move_download", false) ? MoveMode::Download : MoveMode::Files;
        // A move saved without a valid stage counts as unchecked. Earlier
        // versions saved 2 for a checked move that waited for the person. A
        // move that gives its files back is saved as 2 with `move_returns`,
        // so an earlier version waits for the person instead of continuing
        // the move.
        auto stage = ReadSaved(saved, "move_stage", 0);
        bool returns = stage == 2 && ReadSaved(saved, "move_returns", false);
        if (returns)
            facts.marker->stage = MoveStage::Returning;
        else if (stage == 1 || stage == 2)
            facts.marker->stage = MoveStage::Placing;
        facts.marker->failed = (stage == 2 && !returns) || ReadSaved(saved, "move_failed", false);
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
            auto saved = Whole<std::uint8_t>(value);
            facts.priorities_.push_back(saved ? NearestChoice(*saved) : lt::default_priority);
        }
    }
    if (auto found = saved.find("question"); found != saved.end() && found->is_object())
    {
        facts.question_.number = ReadSaved(*found, "number", std::uint64_t(0));
        if (auto files = found->find("files"); files != found->end() && files->is_array())
        {
            for (auto const& entry : *files)
            {
                if (!entry.is_object())
                    continue;
                auto index = ReadSaved(entry, "index", -1);
                auto value = entry.find("priority");
                auto saved = value != entry.end() ? Whole<std::uint8_t>(*value) : std::nullopt;
                auto priority = saved ? NearestChoice(*saved) : lt::dont_download;
                // The question holds only files that the saved priorities
                // skip, each with a priority that downloads.
                if (index < 0 || static_cast<std::size_t>(index) >= facts.priorities_.size() ||
                    facts.priorities_[index] != lt::dont_download || priority == lt::dont_download)
                    continue;
                facts.question_.files.emplace(lt::file_index_t(index), priority);
            }
        }
    }
    facts.repairs_ = ReadSaved(saved, "repairs", false);
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

Torrent::Torrent(std::string torrentId, WorkQuery work) : torrentId(std::move(torrentId)), work_(std::move(work))
{
}

std::optional<tt::PathWork> Torrent::PathWork() const
{
    return work_(torrentId);
}

std::string Torrent::Name() const
{
    return Name(Hashes());
}

std::string Torrent::Name(std::vector<std::string> const& hashes) const
{
    return tt::Name(status.name, hashes);
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
    auto metadata = Metadata();
    if (!metadata)
        return Utf8(folder.wstring());
    std::optional<std::filesystem::path> first;
    bool multiple = false;
    // The files share no top folder.
    bool apart = false;
    VisitContent(metadata->layout(), facts.layout, [&](lt::file_index_t, std::string const& name)
    {
        auto path = std::filesystem::path(Wide(name));
        if (!first)
        {
            first = std::move(path);
            return true;
        }
        multiple = true;
        apart = !first->has_parent_path() || *path.begin() != *first->begin();
        return !apart;
    });
    if (first && !apart)
        folder = multiple ? folder / *first->begin() : (folder / *first).parent_path();
    return Utf8(folder.wstring());
}

std::vector<std::string> Torrent::Tops(bool logical) const
{
    return tt::Tops([this, logical](auto const& visit) { VisitPaths(logical, visit); });
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
    if (waitingMove && waitingMove->refusal)
        return waitingMove->refusal;
    if (waiting && waiting->refusal)
        return waiting->refusal;
    if (control.Cause() || nameError)
        return StorageProblem();
    // A waiting move the person asked for replaces the interrupted one.
    if (!PathWork() && !waitingMove && facts.marker)
    {
        return Problem{unfinished ? ProblemKind::MoveUnfinished : ProblemKind::MoveInterrupted,
            facts.marker->destination};
    }
    if (!conflict.empty())
    {
        return Problem{ProblemKind::AliasConflict, conflict};
    }
    return StorageProblem();
}

std::optional<Problem> Torrent::StorageProblem() const
{
    if (auto cause = control.Cause())
        return cause;
    if (nameError)
        return nameError;
    if (status.errc)
    {
        return Problem::From(status.errc);
    }
    if (diskError)
    {
        return Problem::From(diskError);
    }
    if (bool(status.flags & lt::torrent_flags::upload_mode) && !status.is_seeding)
    {
        return Problem{ProblemKind::TorrentError};
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

bool Torrent::NeedsPerson() const
{
    // libtorrent's error for an alias conflict belongs to the conflict, not
    // to the storage control, and waits for the person too.
    return control.NeedsPerson() || (waiting && waiting->refusal) || (!conflict.empty() && status.errc);
}

bool Torrent::MoveWaits() const
{
    return !PathWork() && (waitingMove || unfinished == TaskKind::Move) && !Error();
}

bool Torrent::ShowsPathWork() const
{
    auto work = PathWork();
    return work && !(work == tt::PathWork::Abandon && waitingMove && waitingMove->refusal);
}

bool Torrent::FileWork() const
{
    return PathWork() || NameWork();
}

bool Torrent::NameWork() const
{
    return namePhase == NamePhase::Preparing || namePhase == NamePhase::Recovering || !renaming.empty() ||
        !marking.empty();
}

bool Torrent::FilesBusy() const
{
    return control.Busy() || FileWork();
}

bool Torrent::WorkWaits() const
{
    return waiting || control.Recovers() || FileWork() || look == LookPhase::Wanted || look == LookPhase::Running;
}

bool Torrent::IsHeld(bool hasMetadata) const
{
    // A stopped torrent that stayed auto-managed would leave upload mode by
    // itself every few minutes and fail its writes again.
    return control.Holds() || !conflict.empty() || PathWork() || facts.marker || cacheWrite == CacheWrite::Flushing ||
        CheckWaits() || unfinished == TaskKind::Check || (hasMetadata && namePhase != NamePhase::Ready) || kept;
}

bool Torrent::CheckWaits() const
{
    return checkTurn && !checkTurn->running;
}

bool Torrent::NamesKnown() const
{
    return namePhase != NamePhase::Recovering && (waiting || !handle.torrent_file() || names);
}

std::vector<std::string> Torrent::Folders() const
{
    std::vector<std::string> folders{facts.savePath};
    if (facts.marker)
        folders.push_back(facts.marker->destination);
    return folders;
}

bool Torrent::HeldByAlias() const
{
    return waiting && !conflict.empty();
}

void Torrent::Attach(lt::torrent_handle added, StorageRoot root)
{
    handle = std::move(added);
    storageRoot = std::move(root);
    waiting.reset();
}

void Torrent::WaitForAdd(lt::add_torrent_params params)
{
    handle = {};
    waiting = Waiting{std::move(params)};
}

bool Torrent::AwaitsAdd() const
{
    return waiting && !waiting->refusal && conflict.empty();
}

std::shared_ptr<lt::torrent_info const> Torrent::Metadata() const
{
    return waiting ? waiting->params.ti : handle.torrent_file();
}

bool Torrent::ChecksFiles(bool sessionPaused) const
{
    return !sessionPaused && status.state == lt::torrent_status::checking_files &&
        !(status.flags & lt::torrent_flags::paused) && !PathWork() && !CheckWaits();
}

bool Torrent::SeedLimited() const
{
    return status.is_finished && facts.intent != Intent::Paused && !facts.ignoresSeedLimits;
}

bool Torrent::Done() const
{
    return status.has_metadata && status.is_finished && facts.Asked().files.empty();
}

bool Torrent::MoveBlocked() const
{
    return moveError.has_value();
}

void Torrent::AnswerMoveProblem()
{
    if (!facts.marker)
        moveError.reset();
}

Status Torrent::Classify(bool sessionPaused) const
{
    if (ShowsPathWork())
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
    if (status.is_finished && !Done())
    {
        return Status::FilesMissing;
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

void Torrent::Update(lt::torrent_status latest, lt::time_point at)
{
    completion.Sampled(status, latest);
    status = std::move(latest);
    statusAt = at;
    if (status.state != lt::torrent_status::checking_resume_data)
        savedProgress.reset();
}

FileProgress FileProgress::Of(lt::torrent_status const& status)
{
    return {status.total_wanted_done, status.is_finished ? status.all_time_upload : 0,
        status.state == lt::torrent_status::checking_files ? status.progress_ppm : 0};
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
    received_ |= replaced_ && latest.total_payload_download > 0;
    received_ |= phase_ == CompletionPhase::Downloading &&
        latest.total_payload_download > previous.total_payload_download;
}

bool Completion::Observed(lt::torrent_status const& previous, lt::torrent_status const& latest)
{
    requested_ = false;
    if (std::exchange(replaced_, false) && latest.total_payload_download > 0)
    {
        phase_ = CompletionPhase::Downloading;
        received_ = true;
    }
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
    replaced_ = false;
}

void Completion::Replaced()
{
    received_ = false;
    replaced_ = true;
    origin_.reset();
    requested_ = true;
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

bool Completion::IsReached(bool done) const
{
    return done && !IsWorking();
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
    // One number per file would make the reply grow with the torrent; file
    // pages carry each file's choice. The question and the repair mark are
    // the engine's own.
    data.erase("priorities");
    data.erase("question");
    data.erase("repairs");
    data.update(Describe(TorrentView::General, detail, metadata));
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

template <typename Text>
void Append(Text& text, std::string_view part)
{
    text.insert(text.end(), part.begin(), part.end());
}

template <typename Text, typename Number>
void AppendNumber(Text& text, Number value)
{
    char buffer[32];
    auto end = std::to_chars(buffer, std::end(buffer), value).ptr;
    text.insert(text.end(), buffer, end);
}

// "address:port", with an IPv6 address in brackets.
template <typename Text>
void AppendEndpoint(Text& text, lt::tcp::endpoint const& endpoint)
{
    auto v6 = endpoint.address().is_v6();
    if (v6) text.push_back('[');
    Append(text, endpoint.address().to_string());
    if (v6) text.push_back(']');
    text.push_back(':');
    AppendNumber(text, endpoint.port());
}

std::string DescribeEndpoint(lt::tcp::endpoint const& endpoint)
{
    std::string text;
    AppendEndpoint(text, endpoint);
    return text;
}

using Bytes = std::vector<std::uint8_t>;

// A peer names its own client, so the name can hold any bytes. The JSON
// library writes anything but plain ASCII, escaping it and replacing invalid
// UTF-8 as the pipe does.
void AppendString(Bytes& text, std::string_view value)
{
    auto plain = std::all_of(value.begin(), value.end(),
        [](char c) { return c >= ' ' && c <= '~' && c != '"' && c != '\\'; });
    if (!plain)
    {
        Append(text, Json(std::string(value)).dump(-1, ' ', false, Json::error_handler_t::replace));
        return;
    }
    text.push_back('"');
    Append(text, value);
    text.push_back('"');
}

// `"name":`, after a comma unless it starts the object.
void AppendName(Bytes& text, std::string_view name)
{
    if (text.back() != '{') text.push_back(',');
    text.push_back('"');
    Append(text, name);
    Append(text, "\":");
}

void AppendBool(Bytes& text, std::string_view name, bool value)
{
    AppendName(text, name);
    Append(text, value ? "true" : "false");
}

// The names of the flags that are set, as a JSON array.
template <typename Flags>
void AppendFlags(Bytes& text, std::string_view name, Flags flags,
    std::initializer_list<std::pair<Flags, std::string_view>> names)
{
    AppendName(text, name);
    text.push_back('[');
    for (auto const& [flag, word] : names)
    {
        if (!bool(flags & flag)) continue;
        if (text.back() != '[') text.push_back(',');
        AppendString(text, word);
    }
    text.push_back(']');
}

void AppendWait(Bytes& text, std::string_view name, lt::bandwidth_state_flags_t state)
{
    AppendFlags(text, name, state, {{lt::peer_info::bw_limit, "limit"}, {lt::peer_info::bw_network, "network"},
        {lt::peer_info::bw_disk, "disk"}});
}

void AppendSources(Bytes& text, lt::peer_source_flags_t source)
{
    AppendFlags(text, "sources", source, {{lt::peer_info::tracker, "tracker"}, {lt::peer_info::dht, "dht"},
        {lt::peer_info::pex, "pex"}, {lt::peer_info::lsd, "lsd"}, {lt::peer_info::resume_data, "resume"},
        {lt::peer_info::incoming, "incoming"}});
}

// The torrent's connected peers, then the known peers that are not connected,
// written as JSON text: a large swarm needs no JSON object per peer.
Json DescribePeers(Detail const& detail)
{
    Bytes text;
    text.reserve(detail.peers.size() * 512 + detail.knownPeers.size() * 96 + 2);
    text.push_back('[');
    std::vector<lt::tcp::endpoint> connected;
    connected.reserve(detail.peers.size());
    for (auto const& peer : detail.peers)
    {
        if (text.back() != '[') text.push_back(',');
        text.push_back('{');
        AppendName(text, "endpoint");
        std::string_view transport = bool(peer.flags & lt::peer_info::utp_socket) ? "utp" : "tcp";
        if (bool(peer.flags & lt::peer_info::i2p_socket))
        {
            transport = "i2p";
#if TORRENT_USE_I2P
            AppendString(text, lt::aux::to_hex(peer.i2p_destination().to_string()));
#else
            AppendString(text, "");
#endif
        }
        else
        {
            text.push_back('"');
            AppendEndpoint(text, peer.remote_endpoint());
            text.push_back('"');
            connected.push_back(peer.remote_endpoint());
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
        AppendName(text, "client");
        AppendString(text, peer.client);
        AppendName(text, "role");
        AppendString(text, bool(peer.flags & lt::peer_info::seed) ? "seed" :
            state == PeerState::Connected && !webSeed ? "leecher" : "unknown");
        AppendName(text, "transport");
        AppendString(text, transport);
        AppendBool(text, "incoming", !bool(peer.flags & lt::peer_info::outgoing_connection));
        AppendName(text, "state");
        AppendString(text, ToString(state));
        AppendName(text, "encryption");
        AppendString(text, ToString(encryption));
        AppendBool(text, "interested", bool(peer.flags & lt::peer_info::interesting));
        AppendBool(text, "choked", bool(peer.flags & lt::peer_info::choked));
        AppendBool(text, "remote_interested", bool(peer.flags & lt::peer_info::remote_interested));
        AppendBool(text, "remote_choked", bool(peer.flags & lt::peer_info::remote_choked));
        AppendWait(text, "read_wait", peer.read_state);
        AppendWait(text, "write_wait", peer.write_state);
        AppendSources(text, peer.source);
        AppendName(text, "progress");
        AppendNumber(text, peer.progress_ppm / 1'000'000.0);
        AppendName(text, "download_rate");
        AppendNumber(text, peer.payload_down_speed);
        AppendName(text, "upload_rate");
        AppendNumber(text, peer.payload_up_speed);
        AppendName(text, "downloaded");
        AppendNumber(text, peer.total_download);
        AppendName(text, "uploaded");
        AppendNumber(text, peer.total_upload);
        text.push_back('}');
    }
    std::sort(connected.begin(), connected.end());
    for (auto const& peer : detail.knownPeers)
    {
        if (std::binary_search(connected.begin(), connected.end(), peer.endpoint)) continue;
        if (text.back() != '[') text.push_back(',');
        Append(text, "{\"endpoint\":\"");
        AppendEndpoint(text, peer.endpoint);
        Append(text, "\",\"state\":\"known\"");
        AppendName(text, "role");
        AppendString(text, peer.seed ? "seed" : "unknown");
        AppendSources(text, peer.source);
        text.push_back('}');
    }
    text.push_back(']');
    return Fragment(std::move(text));
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

// Names, sizes and pieces come in pages from the `files` command, and the
// values that change while it downloads come for the rows the browser names.
Json DescribeFiles(std::shared_ptr<lt::torrent_info const> const& metadata)
{
    return {{"file_count", metadata ? metadata->num_files() : 0}};
}

Json DescribePieces(Torrent const& torrent, std::shared_ptr<lt::torrent_info const> const& metadata,
    Detail const& detail)
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
            auto pieces = Pieces(*metadata, index);
            std::fill(wanted.begin() + pieces.first, wanted.begin() + pieces.end, true);
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
    return data;
}
}

Json Torrent::DescribeFilePage(std::shared_ptr<lt::torrent_info const> const& metadata, int offset, bool paths) const
{
    Json data = {{"torrent_id", torrentId}, {"offset", offset}, {"file_count", metadata ? metadata->num_files() : 0},
        {"files", Json::array()}};
    if (!metadata)
    {
        return data;
    }
    auto const& storage = metadata->layout();
    auto masks = ChoiceMasks();
    auto& files = data["files"];
    std::size_t names = 0;
    for (int item = offset; item < storage.num_files() && files.size() < filePage && names < filePageNames; ++item)
    {
        lt::file_index_t index(item);
        auto pieces = Pieces(*metadata, index);
        Json file = {{"index", item}, {"size", storage.file_size(index)}, {"padding", storage.pad_file_at(index)},
            {"first_piece", pieces.first}, {"end_piece", pieces.end},
            {"priority", static_cast<std::uint8_t>(ChosenPriority(storage, index, masks))}};
        if (paths)
        {
            auto path = storage.file_path(index);
            names += path.size();
            file["path"] = std::move(path);
            file["disk_path"] = DiskPath(index);
        }
        files.push_back(std::move(file));
    }
    return data;
}

Json Torrent::DiskPath(lt::file_index_t index) const
{
    return names && names->metadata ? Json(names->mappings.file_path(names->metadata->layout(), index)) : Json();
}

Json Torrent::Describe(TorrentView view, Detail const& detail,
    std::shared_ptr<lt::torrent_info const> const& metadata,
    std::chrono::steady_clock::time_point since) const
{
    Json data = {{"torrent_id", torrentId}, {"metadata_ready", bool(metadata)}};
    switch (view)
    {
    case TorrentView::Peers:
        if (detail.Has(DetailKind::Peers, since))
            data["peers"] = DescribePeers(detail);
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
        data.update(DescribeFiles(metadata));
        break;
    case TorrentView::Pieces:
        if (!metadata || (detail.Has(DetailKind::Status, since) &&
            detail.Has(DetailKind::Availability, since) &&
            detail.Has(DetailKind::Downloading, since)))
            data.update(DescribePieces(*this, metadata, detail));
        break;
    }
    return data;
}

Json Torrent::Row(Status shown, int queue, std::optional<double> moveProgress, DriveWait const& wait,
    bool retry) const
{
    auto problem = Diagnose();
    auto complete = completion.IsReached(Done());
    auto saved = status.state == lt::torrent_status::checking_resume_data ? savedProgress : std::nullopt;
    auto hashes = Hashes();
    return {{"torrent_id", torrentId}, {"save_path", facts.savePath}, {"final_folder", facts.finalFolder},
        {"moving", ShowsPathWork()}, {"move_destination", facts.marker ? facts.marker->destination : std::string()},
        {"paused", facts.intent == Intent::Paused}, {"forced", facts.intent == Intent::Forced},
        {"sequential", facts.sequential}, {"first_last", facts.firstLast},
        {"download_limit", facts.downloadLimit}, {"upload_limit", facts.uploadLimit},
        {"added", facts.added}, {"name", Name(hashes)}, {"size", status.total_wanted},
        {"completed", saved ? std::int64_t(double(*saved) * double(status.total_wanted)) : status.total_wanted_done},
        {"progress", moveProgress.value_or(saved.value_or(status.progress))}, {"status", ToString(shown)},
        {"waiting_drive", wait.drive}, {"waiting_torrent_id", wait.holderId},
        {"waiting_files", wait.files}, {"waiting_answer", wait.answer},
        {"download_rate", status.download_payload_rate}, {"upload_rate", status.upload_payload_rate},
        {"error", !problem ? "" : problem->Code()},
        {"detail", problem ? problem->detail : ""}, {"retry", retry},
        {"seed_count", status.num_seeds}, {"peer_count", status.num_peers},
        {"known_peer_count", status.list_peers},
        // The tracker's scrape counts the whole swarm; without one, the peers this session has heard of
        // are the best estimate.
        {"swarm_seed_count", status.num_complete >= 0 ? status.num_complete : status.list_seeds},
        {"swarm_leecher_count", status.num_incomplete >= 0 ? status.num_incomplete : status.list_peers - status.list_seeds},
        {"downloaded", status.all_time_download}, {"uploaded", status.all_time_upload},
        {"queue", queue},
        {"complete", complete}, {"completed_on", complete ? status.completed_time : 0},
        {"incoming", status.has_incoming}, {"hashes", std::move(hashes)}};
}

// Choices that wait for busy files are saved, so they show before libtorrent
// has them.
std::optional<std::vector<lt::download_priority_t>> Torrent::ShownPriorities(Detail const& detail) const
{
    if (prioritiesPending)
        return facts.Priorities();
    if (detail.Has(DetailKind::Priorities))
        return detail.priorities;
    return std::nullopt;
}

std::vector<std::wstring> Torrent::ChoiceMasks() const
{
    return facts.Priorities().empty() ? SkipMasks(facts.skipPatterns) : std::vector<std::wstring>();
}

std::vector<lt::download_priority_t> Torrent::ChosenPriorities(
    std::shared_ptr<lt::torrent_info const> const& metadata) const
{
    if (!metadata)
    {
        return std::vector<lt::download_priority_t>(facts.Priorities().size(), lt::dont_download);
    }
    auto masks = ChoiceMasks();
    std::vector<lt::download_priority_t> priorities;
    for (auto index : metadata->layout().file_range())
    {
        priorities.push_back(ChosenPriority(metadata->layout(), index, masks));
    }
    return priorities;
}

lt::download_priority_t Torrent::ChosenPriority(lt::file_storage const& files, lt::file_index_t index,
    std::vector<std::wstring> const& masks) const
{
    auto const& priorities = facts.Priorities();
    if (priorities.empty())
    {
        return DefaultPriority(files, index, masks);
    }
    // Only a damaged store saves a list of another length; a file it misses
    // takes the priority libtorrent gives a file without one.
    auto item = static_cast<std::size_t>(static_cast<int>(index));
    return item < priorities.size() ? priorities[item] : DefaultPriority(files, index, {});
}

void Torrent::ApplyIntent()
{
    if (waiting)
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
    if (facts.intent == Intent::Paused || (metadata && facts.Priorities().empty() &&
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

void Torrent::Retry()
{
    conflict.clear();
    if (waiting)
        waiting->refusal.reset();
}

void Torrent::ClearDiskError()
{
    diskClearedAt = lt::clock_type::now();
    if (!waiting)
    {
        handle.clear_error();
        handle.unset_flags(lt::torrent_flags::upload_mode);
    }
    diskError.clear();
    // The last sample would show the cleared error until the next one.
    status.errc.clear();
    status.flags &= ~lt::torrent_flags::upload_mode;
}

// Each piece gets the highest priority of the wanted files it holds, as
// libtorrent gives it. With firstLast the end pieces of each wanted file get
// the top priority: 1 % of the file at each end, at least one
// piece, which covers a media header and an AVI index.
void Torrent::PrioritizePieces(std::vector<lt::download_priority_t> const& priorities) const
{
    if (waiting)
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
        auto range = Pieces(*metadata, index);
        auto first = static_cast<int>(range.first);
        auto last = static_cast<int>(range.end) - 1;
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
    if (waiting)
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
