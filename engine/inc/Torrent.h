#pragma once

#include "Engine.h"
#include "Enums.h"
#include "SpeedHistory.h"
#include <libtorrent/add_torrent_params.hpp>
#include <libtorrent/announce_entry.hpp>
#include <libtorrent/peer_info.hpp>
#include <libtorrent/torrent_handle.hpp>
#include <libtorrent/torrent_status.hpp>
#include <chrono>
#include <cstdint>
#include <filesystem>
#include <map>
#include <optional>
#include <set>
#include <string>
#include <vector>

namespace tt
{
struct Problem
{
    ProblemKind kind;
    std::string detail;
    // Why a move did not start. The row and the notice report it instead of
    // the kind, so the window can say why.
    std::optional<ErrorCode> refusal;

    char const* Code() const;
};

char const* ToString(ProblemKind kind);

// What the document saves about a torrent, apart from its identity.
struct Facts
{
    std::string savePath;
    std::string finalFolder;
    bool appendsSuffix = true;
    Layout layout = Layout::Keep;
    std::string skipPatterns;
    std::string moveDestination;
    bool verifyFiles = false;
    Intent intent = Intent::Resumed;
    bool ignoresSeedLimits = false;
    // When it was added, in seconds since 1970.
    std::int64_t added = 0;
    // Empty means the default priorities, once the metadata is known.
    std::vector<lt::download_priority_t> priorities;
    std::vector<std::string> hashes;
    // No choice retains the resume trackers; an empty choice removes them.
    std::optional<std::vector<lt::announce_entry>> trackers;
    bool sequential = false;
    bool firstLast = false;
    // In bytes a second; 0 means no limit.
    int downloadLimit = 0;
    int uploadLimit = 0;

    Json ToJson() const;
    static Facts Read(Json const& saved);
};

// Reads file priorities as the add command sends them. Throws when a value is
// not a whole number from 0 to 255.
std::vector<lt::download_priority_t> ReadPriorities(Json const& values);

// libtorrent's latest answers to the detail queries for one torrent.
struct Detail
{
    bool Has(DetailKind kind, std::chrono::steady_clock::time_point since = {}) const
    {
        auto found = received.find(kind);
        return found != received.end() && found->second >= since;
    }
    // When each kind's answer arrived; a kind without one has no answer.
    std::map<DetailKind, std::chrono::steady_clock::time_point> received;
    std::vector<lt::announce_entry> trackers;
    std::vector<lt::peer_info> peers;
    std::vector<std::int64_t> progress;
    std::vector<lt::download_priority_t> priorities;
    lt::torrent_status status;
    std::vector<int> availability;
    // The bytes received of each piece being downloaded.
    std::map<lt::piece_index_t, std::int64_t> downloading;
};

struct FileNames
{
    std::shared_ptr<lt::torrent_info const> metadata;
    lt::renamed_files mappings;
};

// A torrent in the list: its libtorrent handle, the facts the document saves
// about it, and what the engine knows about its errors and checkpoints.
struct Torrent
{
    std::string torrentId;
    lt::torrent_handle handle;
    Facts facts;
    // A conflicting restored identity keeps its parameters until its other
    // owner is removed; it must never borrow that owner's live handle.
    std::optional<lt::add_torrent_params> restore;
    std::optional<FileNames> names;
    std::string comment;
    std::string creator;
    std::int64_t created = 0;
    // The add/resume list applies until the person chooses a replacement.
    std::vector<lt::announce_entry> sourceTrackers;
    lt::torrent_status status;
    SpeedHistory history;
    CheckpointPhase checkpointPhase = CheckpointPhase::Idle;
    std::optional<lt::add_torrent_params> pendingCheckpoint;
    bool unsaved = true;
    std::chrono::steady_clock::time_point retryAt{};
    std::int64_t savedUploaded = 0;
    std::optional<Problem> checkpointError;
    std::optional<Problem> hashError;
    std::string conflict;
    std::string diskError;
    std::string notifiedError;
    bool receivedPayload = false;
    std::optional<lt::torrent_status::state_t> transferState;
    NamePhase namePhase = NamePhase::Pending;
    bool needsRecheck = false;
    std::set<lt::file_index_t> completedFiles;
    bool filesPending = false;
    std::set<lt::file_index_t> renaming;
    std::chrono::steady_clock::time_point renameAt{};
    // While Flushing, the torrent does not show as complete.
    CompletionPhase completionPhase = CompletionPhase::Idle;
    bool completionPending = false;
    Reply priorityReply;
    bool piecesPending = false;
    bool moving = false;
    std::optional<Problem> moveError;
    // The person deleted the torrent before the engine could remove it. It is
    // out of the list, stays paused, and refuses commands until it is removed.
    bool deleted = false;
    DeletionMode deletionMode = DeletionMode::Permanent;
    // Rises with each reply to a command that changes the torrent, and when
    // its metadata arrives, so no detail queried before then is shown.
    std::uint64_t generation = 0;
    // The generation at which each outstanding detail query was sent. An
    // answer carries only its torrent, so one query of each kind is
    // outstanding at a time and the record stays until its answer arrives.
    std::map<DetailKind, std::uint64_t> queries;

    std::string Name() const;
    // Every hash the torrent is known by: its own and those the document saved.
    std::vector<std::string> Hashes() const;
    std::string Folder() const;
    std::vector<std::filesystem::path> Paths(bool logical = false) const;
    std::optional<Problem> Error() const;
    std::optional<Problem> Diagnose() const;
    // The torrent's own files are being prepared, renamed or moved.
    bool FilesBusy() const;
    bool MoveBlocked() const;
    Status Classify(bool sessionPaused) const;
    bool IsChanged() const;
    // Takes the newest status, and remembers when it shows new payload.
    void Update(lt::torrent_status latest);
    // The torrent's facts with its General and Files detail.
    Json Describe(Detail const& detail, std::shared_ptr<lt::torrent_info const> const& metadata) const;
    Json Describe(TorrentView view, bool includeFiles, Detail const& detail,
        std::shared_ptr<lt::torrent_info const> const& metadata,
        std::chrono::steady_clock::time_point since = {}) const;
    Json Row(bool sessionPaused) const;
    std::vector<lt::download_priority_t> ChosenPriorities(
        std::shared_ptr<lt::torrent_info const> const& metadata) const;
    std::vector<lt::announce_entry> const& Trackers() const;
    void ApplyIntent();
    // libtorrent sets piece priorities again from the file priorities each
    // time those change, so this runs again after each change settles.
    void PrioritizePieces(std::vector<lt::download_priority_t> const& priorities) const;
    void Checkpoint(bool force);
};

// Rules about torrent content that previews share with torrents.
// The hashes as hexadecimal text, v1 first.
std::vector<std::string> Hashes(lt::info_hash_t const& hashes);
std::string Name(std::string name, std::vector<std::string> const& hashes);
lt::download_priority_t DefaultPriority(lt::file_storage const& files, lt::file_index_t index);
std::vector<lt::download_priority_t> DefaultPriorities(lt::file_storage const& files, std::string const& patterns = {});
std::string ContentPath(lt::file_storage const& files, lt::file_index_t index, Layout layout);
Json Files(std::shared_ptr<lt::torrent_info const> const& metadata);
std::vector<std::string> Urls(std::vector<lt::announce_entry> const& trackers);
}
