#pragma once

#include "Engine.h"
#include <libtorrent/units.hpp>
#include <array>
#include <cstdint>
#include <filesystem>
#include <map>
#include <optional>

namespace tt
{
// What the engine remembers of a finished file of a torrent, so it knows the
// file again without reading it: its identity on its volume and its last
// write. A program that changes a file in place keeps its ID, so the time
// shows the change.
struct Record
{
    // The volume's serial number, as Windows gives it with the file ID.
    std::uint64_t volume = 0;
    // Nothing on a volume without stable file IDs, such as FAT or exFAT.
    std::optional<std::array<std::uint8_t, 16>> id;
    // In Windows file time.
    std::int64_t written = 0;

    bool operator==(Record const&) const = default;

    // The record of the file at `path`; nothing when Windows cannot open it.
    static std::optional<Record> Take(std::filesystem::path const& path);
    // The file at `path` is the recorded file, unchanged, with `size` bytes.
    bool Proves(std::filesystem::path const& path, std::int64_t size) const;
    // The file at `path` has `size` bytes and the recorded time, which is what
    // a copy on another volume keeps of the recorded file.
    bool Matches(std::filesystem::path const& path, std::int64_t size) const;
    // Where the recorded file is now, found by its ID on the volume of
    // `folder`, or of the nearest folder above it that exists. Nothing when the
    // file is gone or in the Recycle Bin, when `folder` is on another volume,
    // and when the record has no ID.
    std::optional<std::filesystem::path> Locate(std::filesystem::path const& folder) const;
};

// A torrent's records, by file. Its records file beside its resume data holds
// them, so the facts document stays small.
using Records = std::map<lt::file_index_t, Record>;

// What a records file holds: the records, and whether a records pass was due
// when it was written, so the next start runs it.
struct SavedRecords
{
    Records records;
    bool pass = false;
};

Json ToJson(SavedRecords const& saved);
// An entry that the file holds incorrectly is left out; that file is checked
// instead of proven.
SavedRecords ReadRecords(Json const& saved);
}
