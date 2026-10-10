#pragma once

#include "Enums.h"
#include <nlohmann/json.hpp>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <functional>
#include <limits>
#include <memory>
#include <optional>
#include <string>
#include <string_view>
#include <type_traits>
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
constexpr wchar_t repairClass[] = L"--repair-class";
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
    // The code of the problem, translated by the surface showing the notice;
    // empty when the notice carries no problem code.
    std::string code;
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
    unsigned activeCount = 0;
    unsigned queuedCount = 0;
    unsigned errorCount = 0;
    std::size_t torrentCount = 0;
    bool paused = false;
    // The tray's Pause item offers Resume only for a pause Resume can lift.
    bool pausedByChoice = false;
    std::string missingAdapter;
    bool downloading = false;
    bool seeding = false;
    bool hasIncoming = false;
    bool notificationsEnabled = false;
    bool notifiesProblems = true;
    bool notifiesAdded = false;
    bool notifiesBackground = true;
    bool preventsSleep = true;
    bool preventsSleepSeeding = false;
    std::vector<std::string> reportedPrograms;
    bool filesBusy = false;
    bool confirmsExit = true;
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

    void Execute(Json const& request, std::string const& connectionId, Reply reply);
    void Tick();
    void Disconnect(std::string const& connectionId);
    // The completion receives nothing when the final save succeeded, and
    // otherwise its cause, which is empty when the cause is unknown.
    void Shutdown(std::function<void(std::optional<std::string> failure)> completion);
    void PauseSession(bool paused, std::function<void(Outcome)> completion);
    void SetBackgroundNotification(bool enabled, std::function<void(Outcome)> completion);
    // Saves the missing programs that torrent handlers start, once reported.
    void RecordPrograms(std::vector<std::string> programs, std::function<void(Outcome)> completion);
    // Adds a source to the default destination with the default file choices.
    void Add(std::string const& source, std::function<void(Outcome, Added)> completion);
    Json Snapshot() const;
    tt::Activity Activity() const;
    std::vector<Notice> TakeNotices();
    std::string Name(std::string const& torrentId) const;
    std::string Folder(std::string const& torrentId) const;
    std::string Language() const;
    std::string Theme() const;
    bool IsShuttingDown() const;
    bool IsLoading() const;
    bool HasStorageFailure() const;
    std::string StartupError() const;
    std::string SessionId() const;
    bool ShowsAdd() const;
    bool RaisesAdd() const;
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

// Reads a value from settings.json, or returns `fallback` when the key is
// missing or holds a value of another type, so a damaged file still loads with
// every value it holds correctly. Every whole number saved there is zero or
// more.
template <typename Value>
Value ReadSaved(Json const& saved, char const* key, Value fallback)
{
    auto found = saved.find(key);
    if (found == saved.end())
    {
        return fallback;
    }
    if constexpr (std::is_same_v<Value, bool>)
    {
        if (!found->is_boolean())
        {
            return fallback;
        }
    }
    else if constexpr (std::is_same_v<Value, std::string>)
    {
        if (!found->is_string())
        {
            return fallback;
        }
    }
    else if (!found->is_number_integer() || *found < 0 || *found > (std::numeric_limits<Value>::max)())
    {
        return fallback;
    }
    return found->get<Value>();
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
char const* ToString(ErrorCode code);
std::optional<ErrorCode> ParseError(std::string_view word);
Json Success(Json data = Json::object());
Json Failure(ErrorCode code, std::string detail = {});
// A request refused because of a torrent's problem reports that problem.
Json Failure(ProblemKind kind, std::string detail = {});
}
