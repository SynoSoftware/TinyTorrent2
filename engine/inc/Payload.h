#pragma once

#include "Enums.h"
#include <atomic>
#include <cstdint>
#include <expected>
#include <filesystem>
#include <string>
#include <vector>

// The disk work on torrent files: moving them between folders and deleting
// them. Callers decide which files; this module decides how.
namespace tt::Payload
{
struct File
{
    std::filesystem::path source;
    std::filesystem::path destination;
};

// The bytes of one move. Its worker writes them while the engine reads them.
struct Progress
{
    std::atomic<std::uint64_t> total{0};
    std::atomic<std::uint64_t> done{0};
};

struct Failure
{
    MoveFailure kind;
    std::string detail;
};

// Puts each file at its destination: a rename on one volume, otherwise a copy
// that keeps its source, which the caller deletes once the new folder is
// saved. A missing source is skipped. Before it copies, it refuses a volume
// without the space its copies need. On failure it deletes its copies and
// renames files back. It blocks until every file is placed or restored.
std::expected<void, Failure> Move(std::vector<File> const& files, Progress& progress);

// Deletes the files permanently on parallel workers, and throws the first
// failure after trying every file. A file that is already gone counts as
// deleted.
void Delete(std::vector<std::filesystem::path> const& files);
}
