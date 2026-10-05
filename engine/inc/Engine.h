#pragma once

#include "Enums.h"
#include <nlohmann/json.hpp>
#include <cstdint>
#include <filesystem>
#include <functional>
#include <memory>
#include <optional>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace tiny
{
using Json = nlohmann::json;
using Reply = std::function<void(Json)>;

constexpr wchar_t productName[] = L"TinyTorrent";
// The window process sets the same AppUserModelID, so Windows treats the
// engine and the window as one application.
constexpr wchar_t appId[] = L"Syno.TinyTorrent";

struct Notice
{
    NoticeKind kind;
    std::string name;
    std::string detail;
    std::string torrentId;
};

// What the session is doing, with the settings that decide how the desktop
// reports it.
struct Activity
{
    std::int64_t downloadRate = 0;
    std::int64_t uploadRate = 0;
    unsigned active = 0;
    unsigned queued = 0;
    std::size_t torrentCount = 0;
    bool allPaused = false;
    std::string missingInterface;
    bool downloading = false;
    bool seeding = false;
    bool hasIncoming = false;
    bool notificationsEnabled = true;
    bool preventSleep = true;
    bool preventSleepSeeding = false;
    bool backgroundNoticeShown = false;
    bool filesBusy = false;
};

class Engine
{
public:
    // Whether the engine accepts `source`, a file path or a magnet link.
    static bool IsSource(std::string const& source);

    Engine(std::filesystem::path directory, std::function<void()> wake);
    ~Engine();
    Engine(Engine const&) = delete;
    Engine& operator=(Engine const&) = delete;

    void Execute(Json const& request, Reply reply);
    void Tick();
    void Disconnect(std::string const& connection);
    // The completion receives nothing when the final save succeeded, and
    // otherwise its cause, which is empty when the cause is unknown.
    void Shutdown(std::function<void(std::optional<std::string> failure)> completion);
    Json Snapshot() const;
    tiny::Activity Activity() const;
    std::vector<Notice> TakeNotices();
    std::string Name(std::string const& torrentId) const;
    std::string Folder(std::string const& torrentId) const;
    std::string Language() const;
    bool IsStopping() const;
    bool IsLoading() const;
    bool HasStorageFailure() const;
    bool ShowsAdd() const;
    std::string DefaultDestination() const;

private:
    class State;
    std::unique_ptr<State> state_;
};

// Finds the value a protocol word names, or nothing for an unknown word.
template <typename Value, std::size_t Size>
std::optional<Value> Parse(std::pair<std::string_view, Value> const (&names)[Size], std::string_view word)
{
    for (auto const& [name, value] : names)
    {
        if (name == word)
        {
            return value;
        }
    }
    return std::nullopt;
}

std::string Utf8(std::wstring const& value);
std::wstring Wide(std::string const& value);
std::wstring Executable();
char const* ToString(NoticeKind kind);
Json Success(Json data = Json::object());
Json Failure(std::string code, std::string detail = {});
}
