#pragma once

#include "Engine.h"
#include "Enums.h"
#include <libtorrent/add_torrent_params.hpp>
#include <libtorrent/torrent_handle.hpp>
#include <libtorrent/torrent_status.hpp>
#include <chrono>
#include <cstdint>
#include <filesystem>
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
};

char const* ToString(ProblemKind kind);

// What the document saves about a torrent, apart from its identity.
struct Facts
{
    std::string savePath;
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

    Json ToJson() const;
    static Facts Read(Json const& saved);
};

// Reads file priorities as the document and the add command write them.
// Throws when a value is not a whole number from 0 to 255.
std::vector<lt::download_priority_t> ReadPriorities(Json const& values);

// A torrent in the list: its libtorrent handle, the facts the document saves
// about it, and what the engine knows about its errors and checkpoints.
struct Torrent
{
    std::string identity;
    lt::torrent_handle handle;
    Facts facts;
    std::string comment;
    std::string creator;
    std::int64_t created = 0;
    lt::torrent_status status;
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
    lt::torrent_status::state_t fileState = lt::torrent_status::checking_resume_data;
    bool namesReady = false;
    bool preparingNames = false;
    bool needsRecheck = false;
    std::set<lt::file_index_t> completedFiles;
    std::set<lt::file_index_t> renaming;
    std::chrono::steady_clock::time_point renameAt{};
    // The torrent finished downloading, and libtorrent is still writing its
    // data to disk; until it ends, the torrent does not show as complete.
    bool flushing = false;
    Reply priorityReply;
    bool moving = false;
    std::optional<Problem> moveError;

    std::string Name() const;
    // Every hash the torrent is known by: its own and those the document saved.
    std::vector<std::string> Hashes() const;
    std::string Folder() const;
    std::vector<std::filesystem::path> Paths(bool logical = false) const;
    std::optional<Problem> Error() const;
    std::optional<Problem> Diagnose() const;
    Status Classify(bool allPaused) const;
    bool IsChanged() const;
    // Takes the newest status, and remembers when it shows new payload.
    void Update(lt::torrent_status latest);
    Json Describe() const;
    Json Describe(TorrentView view, bool includeFiles) const;
    Json Row(bool allPaused) const;
    void ApplyIntent();
    // libtorrent sets piece priorities again from the file priorities each
    // time those change, so this runs again after each change settles.
    void PrioritizePieces() const;
    void Checkpoint(bool exiting);
};

// Rules about torrent content that previews share with torrents.
// The hashes as hexadecimal text, v1 first.
std::vector<std::string> Hashes(lt::info_hash_t const& hashes);
std::string Name(std::string name, std::vector<std::string> const& hashes);
lt::download_priority_t DefaultPriority(lt::file_storage const& files, lt::file_index_t index);
std::vector<lt::download_priority_t> DefaultPriorities(lt::file_storage const& files);
Json Files(std::shared_ptr<lt::torrent_info const> const& metadata);
std::vector<std::filesystem::path> Paths(lt::torrent_info const& metadata);
std::vector<std::string> Urls(std::vector<lt::announce_entry> const& trackers);
}
