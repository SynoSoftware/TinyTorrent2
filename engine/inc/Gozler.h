#pragma once

#include <cstdint>
#include <filesystem>
#include <optional>
#include <stop_token>
#include <string>
#include <vector>

// Finding files with gozler, the file index that plan 13 brings, by running
// its command-line program as plan 13's rules for running it say.
namespace tt::Gozler
{
// A file the search asks for: its whole name and its size.
struct Wanted
{
    std::wstring name;
    std::int64_t size = 0;
};

// What gozler answered.
struct Answer
{
    std::vector<std::filesystem::path> found;
    // gozler still builds or scans again the index of a volume, so files on
    // it may be missing from `found`.
    bool building = false;
};

// The gozler.exe beside the program of the Windows service `gozler`, so no
// folder that anyone can write to decides which program runs; nothing when
// the service is not installed.
std::optional<std::filesystem::path> Program();

// Asks gozler in one call for the files with each name and size, outside the
// Recycle Bin. Nothing when gozler did not answer: its service is stopped, it
// did not answer three tries 5 seconds apart, or a call passed its 30-second
// deadline. A requested stop ends a running call at once.
std::optional<Answer> Find(std::filesystem::path const& program, std::vector<Wanted> const& files,
    std::stop_token stop);
}
