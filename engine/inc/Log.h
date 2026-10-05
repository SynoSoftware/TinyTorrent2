#pragma once

#include "Store.h"
#include <deque>
#include <filesystem>
#include <string>

namespace tt
{
// engine.log in the data folder. Lines wait in memory until Flush hands them
// to the store thread, which appends them.
class Log
{
public:
    Log(Store& store, std::filesystem::path directory);
    // Records one event. A repeat of the line that still waits is dropped.
    void Write(std::string const& kind, std::string const& id, std::string const& code);
    void Flush();
    bool IsFlushed() const;

private:
    Store& store_;
    std::filesystem::path directory_;
    std::deque<std::string> lines_;
    bool flushing_ = false;
};
}
