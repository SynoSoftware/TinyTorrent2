#pragma once

#include "Enums.h"
#include <nlohmann/json.hpp>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <functional>
#include <memory>
#include <optional>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace tt
{
using Json = nlohmann::json;
using Reply = std::function<void(Json)>;

constexpr wchar_t productName[] = L"TinyTorrent";
// The window process sets the same AppUserModelID, so Windows treats the
// engine and the window as one application.
constexpr wchar_t appId[] = L"Syno.TinyTorrent";

// Command-line options of the engine executable. The parser and every command
// that Windows runs later use these names.
namespace option
{
constexpr wchar_t literal[] = L"--";
constexpr wchar_t headless[] = L"--headless";
constexpr wchar_t background[] = L"--background";
constexpr wchar_t exit[] = L"--exit";
constexpr wchar_t registration[] = L"--registration";
constexpr wchar_t data[] = L"--data";
}

constexpr std::string_view magnetScheme = "magnet:";

// URI schemes ignore case, so a source is a magnet link whatever the case of
// its scheme.
inline bool IsMagnet(std::string_view source)
{
    return source.size() >= magnetScheme.size() &&
        _strnicmp(source.data(), magnetScheme.data(), magnetScheme.size()) == 0;
}

// Writes the scheme of a magnet link in lower case, the form that the engine
// compares.
inline void NormaliseMagnet(std::string& source)
{
    if (IsMagnet(source))
    {
        source.replace(0, magnetScheme.size(), magnetScheme);
    }
}

struct Notice
{
    NoticeKind kind;
    std::string name;
    std::string detail;
    std::string torrentId;
    unsigned count = 1;
};

// The completion or failure of an operation. A failure has its error code
// and, when known, its cause.
struct Outcome
{
    std::optional<ErrorCode> error;
    std::string detail;
};

// What an addition did, and the torrent it added or found in the list.
struct Added
{
    AdditionKind kind = AdditionKind::New;
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
    unsigned errors = 0;
    std::size_t torrentCount = 0;
    bool allPaused = false;
    std::string missingInterface;
    bool downloading = false;
    bool seeding = false;
    bool hasIncoming = false;
    bool notificationsEnabled = false;
    bool notifyProblems = true;
    bool notifyAdded = false;
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

    void Execute(Json const& request, std::string const& connection, Reply reply);
    void Tick();
    void Disconnect(std::string const& connection);
    // The completion receives nothing when the final save succeeded, and
    // otherwise its cause, which is empty when the cause is unknown.
    void Shutdown(std::function<void(std::optional<std::string> failure)> completion);
    void PauseSession(bool paused, std::function<void(Outcome)> done);
    void RecordBackgroundNotice(std::function<void(Outcome)> done);
    // Adds a source to the default destination with the default file choices.
    void Add(std::string const& source, std::function<void(Outcome, Added)> done);
    Json Snapshot() const;
    tt::Activity Activity() const;
    std::vector<Notice> TakeNotices();
    std::string Name(std::string const& torrentId) const;
    std::string Folder(std::string const& torrentId) const;
    std::string Language() const;
    std::string Theme() const;
    bool IsStopping() const;
    bool IsLoading() const;
    bool HasStorageFailure() const;
    std::string StartupError() const;
    std::string SessionId() const;
    bool ShowsAdd() const;
    bool HasSettings() const;
    bool ShowsSplash() const;
    bool StartsInTray() const;

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

// The protocol word for a value, from the same table that Parse reads.
template <typename Value, std::size_t Size>
std::string_view Word(std::pair<std::string_view, Value> const (&names)[Size], Value value)
{
    for (auto const& [name, named] : names)
    {
        if (named == value)
        {
            return name;
        }
    }
    return {};
}

std::string Utf8(std::wstring const& value);
std::wstring Wide(std::string const& value);
std::string Base64(std::string_view bytes);
std::wstring Executable();
char const* ToString(NoticeKind kind);
Json Success(Json data = Json::object());
Json Failure(ErrorCode code, std::string detail = {});
// A request refused because of a torrent's problem reports that problem.
Json Failure(ProblemKind kind, std::string detail = {});
}
