#pragma once

#include "Enums.h"
#include <nlohmann/json.hpp>
#include <compare>
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
// One client's pushed messages. A message replaces the client's unsent message
// with the same key, so a reader that falls behind receives only the latest of
// each. Withdrawing a key drops its unsent message.
struct Push
{
    std::function<void(std::string key, Json message)> replace;
    std::function<void(std::string const& key)> withdraw;
};

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

struct MoveRecovery
{
    std::vector<std::string> torrentIds;
    std::string destination;
};

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
    std::optional<MoveRecovery> recovery;
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

    void Execute(Json const& request, std::string const& connectionId, Reply reply, Push push);
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

// The JSON value as a whole number from 0 to the largest `Value`, such as a
// count, an index or a limit; nothing for any other value.
template <typename Value>
std::optional<Value> Whole(Json const& value)
{
    if (!value.is_number_integer() || value < 0 || value > (std::numeric_limits<Value>::max)())
    {
        return std::nullopt;
    }
    return value.get<Value>();
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
        return found->is_boolean() ? found->get<bool>() : fallback;
    }
    else if constexpr (std::is_same_v<Value, std::string>)
    {
        return found->is_string() ? found->get<std::string>() : fallback;
    }
    else
    {
        return Whole<Value>(*found).value_or(fallback);
    }
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
// Orders two names, such as paths, file and volume names or registry classes,
// as Windows compares names: ordinally and without case, by the file system's
// case table.
std::weak_ordering CompareNames(std::wstring_view first, std::wstring_view second);
inline bool SameName(std::wstring_view first, std::wstring_view second)
{
    return CompareNames(first, second) == 0;
}
// `name` in upper case by that case table, so names that compare equal have
// one key.
std::wstring NameKey(std::wstring_view name);
// Whether two paths name one folder: the same text, or one folder on disk
// however each is spelled. Engine::State::SameFolder compares only the text.
bool Equivalent(std::filesystem::path const& first, std::filesystem::path const& second);
// The path Windows gives the open file or folder `handle`, after every link,
// in the form `flags` asks for, such as VOLUME_NAME_DOS; nothing when Windows
// does not give it.
std::optional<std::filesystem::path> FinalPath(void* handle, unsigned long flags);
// The Win32 form of a path with the long-path or NT prefix, as a final path, a
// link's target or a drive letter's device has it: \\?\C:\a and \??\C:\a are
// C:\a, \\?\UNC\nas\a is \\nas\a, and a volume is its drive letter. A path with
// a drive letter stays as it is. Nothing for any other form, such as a UNC
// path without the prefix or a volume without a drive letter.
std::optional<std::filesystem::path> Win32Path(std::wstring path);
// Windows' own words for `error`, a Windows error code, in the person's
// language, because the person reads them; nothing when Windows has none.
// `file` fills the placeholder of a message that names one file, such as
// ERROR_BAD_EXE_FORMAT's; a message with more placeholders must not get it.
std::optional<std::wstring> Reason(unsigned long error, wchar_t const* file = nullptr);
// A failed file's detail: the path and the Reason for `error`, or its number
// when Windows has no words for it.
std::string Describe(std::filesystem::path const& path, unsigned long error);
std::string Base64(std::string_view bytes);
std::wstring Executable();
// The executable of `process`, a Windows process handle with query access;
// nothing when Windows does not reveal it.
std::optional<std::filesystem::path> ProcessImage(void* process);
char const* ToString(ToastKind kind);
char const* ToString(ErrorCode code);
Json Success(Json data = Json::object());
Json Failure(ErrorCode code, std::string detail = {});
// The reply to an operation's outcome: a failure with the outcome's error code
// and detail, or a success that carries `data`.
Json Response(Outcome const& outcome, Json data = Json::object());
// A JSON value already written as text, which the pipe writes as it is, so a
// long list needs no JSON object per item. It is only ever an object's member,
// never an array's item, so the pipe never searches the long arrays of a
// snapshot for one.
Json Fragment(std::vector<std::uint8_t> text);
bool IsFragment(Json const& value);
}
