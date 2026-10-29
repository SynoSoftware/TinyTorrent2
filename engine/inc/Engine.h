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
#include <set>
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
#ifdef _DEBUG
// Starts in the tray and shows a sample toast popup, whatever the toast
// switches say, so its look and More menu can be checked.
constexpr wchar_t toast[] = L"--toast";
#endif
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

struct Toast
{
    struct Condition
    {
        // The key of the condition that counts every torrent that waits for
        // the person's Retry.
        static constexpr char const* stopped = "stopped";

        std::string key;
        ProblemChange change;
    };

    static constexpr std::size_t limit = 32;
    ToastKind kind;
    std::string name;
    std::string detail;
    // The code of the problem, translated by the surface showing the toast;
    // empty when the toast carries no problem code.
    std::string code;
    std::string torrentId;
    // The folder that Open folder shows; an unavailable location names its
    // drive root or UNC share. Empty when the toast offers no folder.
    std::string folder;
    unsigned count = 1;
    // Absent for an event; present for an engine-owned storage condition.
    std::optional<Condition> condition;
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
    std::optional<std::string> missingAdapter;
    bool downloading = false;
    bool seeding = false;
    bool hasIncoming = false;
    // The notification switches that are on.
    std::set<Setting> toasts;
    bool preventsSleep = true;
    bool preventsSleepSeeding = false;
    std::vector<std::string> reportedPrograms;
    bool confirmsExit = true;
    Theme theme = Theme::System;
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
    // Cancels the file work, saves it as tasks for the next start, and saves
    // every torrent. Once the saves succeed, `report` receives Saved, before
    // libtorrent's teardown, which waits seconds for trackers to acknowledge
    // the stop. Abrupt, after a failed save, asks the caller to end the
    // process at once. A second call while saving changes nothing.
    void Shutdown(std::function<void(ShutdownEnd end)> report);
    void PauseSession(bool paused, std::function<void(Outcome)> completion);
    bool IsOn(Setting setting) const;
    static std::optional<std::string_view> SwitchWord(Setting setting);
    static std::optional<Setting> FindSwitch(std::string_view word);
    static std::vector<Setting> NotificationSettings();
    // Saves the selected boolean settings as false in one change.
    void TurnOff(std::vector<Setting> const& switches, std::function<void(Outcome)> completion);
    // Retries every torrent that waits for the person's Retry.
    void RetryAll(std::function<void(Outcome)> completion);
    // Saves the missing programs that torrent handlers start, once reported.
    void RecordPrograms(std::vector<std::string> programs, std::function<void(Outcome)> completion);
    // Adds a source to the default destination with the default file choices.
    void Add(std::string const& source, std::function<void(Outcome, Added)> completion);
    tt::Activity Activity() const;
    std::vector<Toast> TakeToasts();
    std::string Name(std::string const& torrentId) const;
    std::string Folder(std::string const& torrentId) const;
    // The storage conditions reported so far, each as raised, for a window
    // that connects while they last.
    std::vector<Toast> Conditions() const;
    std::string Language() const;
    bool IsShuttingDown() const;
    // Whether work that the last end left unfinished waits for the person's
    // answer.
    bool AwaitsAnswer() const;
    bool IsLoading() const;
    bool HasStorageFailure() const;
    std::string StartupError() const;
    std::string SessionId() const;
    bool ShowsAdd(std::string const& source) const;
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
// Whether two paths name one folder, also when they are spelled differently.
bool SameFolder(std::filesystem::path const& first, std::filesystem::path const& second);
std::string Base64(std::string_view bytes);
std::wstring Executable();
// The executable of `process`, a Windows process handle with query access;
// nothing when Windows does not reveal it.
std::optional<std::filesystem::path> ProcessImage(void* process);
char const* ToString(ToastKind kind);
char const* ToString(ErrorCode code);
Json Success(Json data = Json::object());
Json Failure(ErrorCode code, std::string detail = {});
}
