#include "Log.h"
#include <Windows.h>
#include <cstdio>
#include <fstream>

namespace tt
{
namespace
{
// The lines that wait for Flush; the oldest go first.
constexpr std::size_t lineLimit = 32;
// engine.log moves to engine.previous.log when it would grow past this size.
constexpr std::uintmax_t sizeLimit = 1024 * 1024;
}

Log::Log(Store& store, std::filesystem::path directory) :
    store_(store), directory_(std::move(directory)) {}

void Log::Write(std::string const& kind, std::string const& id, std::string const& code)
{
    auto message = kind + " " + id + " " + code;
    if (!lines_.empty() && lines_.back().ends_with(message + "\n"))
    {
        return;
    }
    SYSTEMTIME time;
    GetSystemTime(&time);
    char stamp[32];
    sprintf_s(stamp, "%04u-%02u-%02uT%02u:%02u:%02uZ ", time.wYear, time.wMonth, time.wDay, time.wHour,
        time.wMinute, time.wSecond);
    if (lines_.size() == lineLimit)
    {
        lines_.pop_front();
    }
    lines_.push_back(std::string(stamp) + message + "\n");
}

void Log::Flush()
{
    if (flushing_ || lines_.empty())
    {
        return;
    }
    std::string text;
    for (auto const& line : lines_)
    {
        text += line;
    }
    lines_.clear();
    flushing_ = true;
    store_.Run([directory = directory_, text = std::move(text)]
    {
        auto current = directory / L"engine.log";
        auto previous = directory / L"engine.previous.log";
        std::error_code error;
        if (std::filesystem::file_size(current, error) + text.size() > sizeLimit && !error)
        {
            std::filesystem::remove(previous, error);
            std::filesystem::rename(current, previous, error);
            if (error)
            {
                return;
            }
        }
        std::ofstream stream(current, std::ios::binary | std::ios::app);
        stream << text;
    }, [this](StorageOutcome) { flushing_ = false; });
}

bool Log::IsFlushed() const
{
    return lines_.empty() && !flushing_;
}
}
