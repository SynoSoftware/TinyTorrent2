#include "Engine.h"
#include "Engine/State.h"
#include <Windows.h>
#include <wincrypt.h>
#include <cwchar>
#include <system_error>
#include <utility>

namespace tt
{
namespace
{
// The longest Windows path, 32,767 characters, with its terminating NUL.
constexpr std::size_t pathLimit = 32'768;
}

std::string Utf8(std::wstring const& value)
{
    if (value.empty())
    {
        return {};
    }
    int size = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(),
        static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (!size)
    {
        throw std::runtime_error("Invalid Unicode");
    }
    std::string text(size, '\0');
    WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(),
        static_cast<int>(value.size()), text.data(), size, nullptr, nullptr);
    return text;
}

std::wstring Wide(std::string const& value)
{
    if (value.empty())
    {
        return {};
    }
    int size = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(),
        static_cast<int>(value.size()), nullptr, 0);
    if (!size)
    {
        throw std::runtime_error("Invalid UTF-8");
    }
    std::wstring text(size, L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(),
        static_cast<int>(value.size()), text.data(), size);
    return text;
}

std::weak_ordering CompareNames(std::wstring_view first, std::wstring_view second)
{
    auto order = CompareStringOrdinal(first.data(), static_cast<int>(first.size()), second.data(),
        static_cast<int>(second.size()), TRUE);
    return order == CSTR_LESS_THAN ? std::weak_ordering::less :
        order == CSTR_EQUAL ? std::weak_ordering::equivalent : std::weak_ordering::greater;
}

// Without LCMAP_LINGUISTIC_CASING, LCMapStringEx cases by the file system's
// rules, the table CompareStringOrdinal compares by.
std::wstring NameKey(std::wstring_view name)
{
    if (name.empty())
    {
        return {};
    }
    auto size = static_cast<int>(name.size());
    std::wstring key(static_cast<std::size_t>(
        LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_UPPERCASE, name.data(), size, nullptr, 0, nullptr, nullptr, 0)),
        L'\0');
    LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_UPPERCASE, name.data(), size, key.data(),
        static_cast<int>(key.size()), nullptr, nullptr, 0);
    return key;
}

bool Equivalent(std::filesystem::path const& first, std::filesystem::path const& second)
{
    std::error_code error;
    return first == second || std::filesystem::equivalent(first, second, error);
}

std::optional<std::filesystem::path> FinalPath(HANDLE handle, DWORD flags)
{
    std::wstring path(MAX_PATH, L'\0');
    for (;;)
    {
        // Too small a buffer returns the size it needs, with the terminator.
        auto length = GetFinalPathNameByHandleW(handle, path.data(), static_cast<DWORD>(path.size()), flags);
        if (length == 0)
        {
            return std::nullopt;
        }
        bool fits = length < path.size();
        path.resize(length);
        if (fits)
        {
            return std::filesystem::path(std::move(path));
        }
    }
}

std::optional<std::filesystem::path> Win32Path(std::wstring path)
{
    for (std::wstring_view prefix : {LR"(\??\)", LR"(\\?\)"})
    {
        if (path.starts_with(prefix))
        {
            path.erase(0, prefix.size());
            break;
        }
    }
    if (path.starts_with(LR"(UNC\)"))
    {
        return std::filesystem::path(LR"(\\)" + path.substr(4));
    }
    if (path.size() >= 2 && path[1] == L':')
    {
        return std::filesystem::path(path);
    }
    if (!path.starts_with(L"Volume{"))
    {
        return std::nullopt;
    }
    auto end = path.find(L'\\');
    auto volume = LR"(\\?\)" + path.substr(0, end) + L'\\';
    auto rest = end == std::wstring::npos ? std::wstring() : path.substr(end + 1);
    std::wstring names(MAX_PATH, L'\0');
    DWORD needed = 0;
    while (!GetVolumePathNamesForVolumeNameW(volume.c_str(), names.data(), static_cast<DWORD>(names.size()), &needed))
    {
        if (GetLastError() != ERROR_MORE_DATA || needed <= names.size())
        {
            return std::nullopt;
        }
        names.resize(needed);
    }
    // The names are a list of strings that ends with an empty one.
    for (auto name = names.c_str(); *name; name += std::wcslen(name) + 1)
    {
        std::wstring_view mount(name);
        if (mount.size() == 3 && mount[1] == L':')
        {
            return std::filesystem::path(mount) / rest;
        }
    }
    return std::nullopt;
}

std::optional<std::wstring> Reason(unsigned long error, wchar_t const* file)
{
    DWORD_PTR inserts[]{reinterpret_cast<DWORD_PTR>(file)};
    DWORD flags = FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_MAX_WIDTH_MASK |
        (file ? FORMAT_MESSAGE_ARGUMENT_ARRAY : FORMAT_MESSAGE_IGNORE_INSERTS);
    LPWSTR text = nullptr;
    auto length = FormatMessageW(flags, nullptr, error, 0, reinterpret_cast<LPWSTR>(&text), 0,
        file ? reinterpret_cast<va_list*>(inserts) : nullptr);
    std::wstring reason = length ? std::wstring(text, length) : std::wstring();
    LocalFree(text);
    while (!reason.empty() && reason.back() == L' ')
    {
        reason.pop_back();
    }
    if (reason.empty())
    {
        return std::nullopt;
    }
    return reason;
}

std::string Describe(std::filesystem::path const& path, unsigned long error)
{
    return Utf8(path.wstring() + L": " + Reason(error).value_or(std::to_wstring(error)));
}

std::string Base64(std::string_view bytes)
{
    if (bytes.empty())
    {
        return {};
    }
    constexpr DWORD format = CRYPT_STRING_BASE64 | CRYPT_STRING_NOCRLF;
    auto data = reinterpret_cast<BYTE const*>(bytes.data());
    auto count = static_cast<DWORD>(bytes.size());
    DWORD size = 0;
    if (!CryptBinaryToStringA(data, count, format, nullptr, &size))
    {
        throw std::system_error(static_cast<int>(GetLastError()), std::system_category(), "CryptBinaryToStringA");
    }
    std::string text(size, '\0');
    CryptBinaryToStringA(data, count, format, text.data(), &size);
    text.resize(size);
    return text;
}

Json Success(Json data)
{
    return {{"ok", true}, {"data", std::move(data)}};
}

namespace
{
constexpr std::pair<std::string_view, ErrorCode> errors[] = {
    {"invalid_request", ErrorCode::InvalidRequest},
    {"unknown_command", ErrorCode::UnknownCommand},
    {"invalid_source", ErrorCode::InvalidSource},
    {"invalid_sources", ErrorCode::InvalidSources},
    {"invalid_destination", ErrorCode::InvalidDestination},
    {"move_destination_unavailable", ErrorCode::MoveDestinationUnavailable},
    {"invalid_priorities", ErrorCode::InvalidPriorities},
    {"invalid_trackers", ErrorCode::InvalidTrackers},
    {"invalid_torrents", ErrorCode::InvalidTorrents},
    {"response_too_large", ErrorCode::ResponseTooLarge},
    {"window_connected", ErrorCode::WindowConnected},
    {"starting", ErrorCode::Starting},
    {"shutting_down", ErrorCode::ShuttingDown},
    {"unavailable", ErrorCode::Unavailable},
    {"overloaded", ErrorCode::Overloaded},
    {"storage_failed", ErrorCode::StorageFailed},
    {"encryption_failed", ErrorCode::EncryptionFailed},
    {"recovery_required", ErrorCode::RecoveryRequired},
    {"files_busy", ErrorCode::FilesBusy},
    {"shared_files", ErrorCode::SharedFiles},
    {"destination_conflict", ErrorCode::DestinationConflict},
    {"destination_in_use", ErrorCode::DestinationInUse},
    {"metadata_unavailable", ErrorCode::MetadataUnavailable},
    {"preview_expired", ErrorCode::PreviewExpired},
    {"preview_failed", ErrorCode::PreviewFailed},
    {"torrent_removed", ErrorCode::TorrentRemoved},
    {"torrent_removing", ErrorCode::TorrentRemoving},
    {"alias_conflict", ErrorCode::AliasConflict},
    {"torrent_error", ErrorCode::TorrentError},
    {"add_failed", ErrorCode::AddFailed},
    {"registration_failed", ErrorCode::RegistrationFailed}};
}

char const* ToString(ErrorCode code)
{
    auto word = Word(errors, code);
    return word.empty() ? "" : word.data();
}

Json Failure(ErrorCode code, std::string detail)
{
    return {{"ok", false}, {"error", {{"code", ToString(code)}, {"detail", std::move(detail)}}}};
}

Json Response(Outcome const& outcome, Json data)
{
    return outcome.error ? Failure(*outcome.error, outcome.detail) : Success(std::move(data));
}

namespace
{
// Marks the binary values that hold JSON text; the engine sends no other binary.
constexpr std::uint64_t fragmentSubtype = 'J';
}

Json Fragment(std::vector<std::uint8_t> text)
{
    return Json::binary(std::move(text), fragmentSubtype);
}

bool IsFragment(Json const& value)
{
    return value.is_binary() && value.get_binary().has_subtype() && value.get_binary().subtype() == fragmentSubtype;
}

std::wstring Executable()
{
    std::wstring path(pathLimit, L'\0');
    auto size = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    if (!size || size == path.size())
    {
        throw std::runtime_error("Cannot locate the engine executable.");
    }
    path.resize(size);
    return path;
}

std::optional<std::filesystem::path> ProcessImage(HANDLE process)
{
    std::wstring path(pathLimit, L'\0');
    auto size = static_cast<DWORD>(path.size());
    if (!QueryFullProcessImageNameW(process, 0, path.data(), &size))
    {
        return std::nullopt;
    }
    path.resize(size);
    return std::filesystem::path(std::move(path));
}

char const* ToString(ToastKind kind)
{
    switch (kind)
    {
    case ToastKind::Completed: return "completed";
    case ToastKind::Error: return "error";
    case ToastKind::Added: return "added";
    case ToastKind::Duplicate: return "duplicate";
    case ToastKind::AddFailed: return "add_failed";
    case ToastKind::DeleteFailed: return "delete_failed";
    case ToastKind::Background: return "background";
    case ToastKind::MissingProgram: return "missing_program";
    case ToastKind::Aggregate: return "aggregate";
    case ToastKind::Unfinished: return "unfinished";
    case ToastKind::Followed: return "followed";
    }
    return "";
}

std::optional<std::string_view> Engine::SwitchWord(Setting setting)
{
    return State::Settings::SwitchFor(setting).word;
}

std::optional<Setting> Engine::FindSwitch(std::string_view word)
{
    for (auto const& entry : State::Settings::Switches())
    {
        if (entry.word == word)
            return entry.setting;
    }
    return std::nullopt;
}

bool Engine::IsOn(Setting setting) const
{
    return state_->settings.*State::Settings::SwitchFor(setting).field;
}

std::vector<Setting> Engine::NotificationSettings()
{
    std::vector<Setting> settings;
    for (auto const& entry : State::Settings::Switches())
    {
        if (entry.word)
            settings.push_back(entry.setting);
    }
    return settings;
}


Engine::Engine(std::filesystem::path directory, std::function<void()> wake) :
    state_(std::make_unique<State>(std::move(directory), std::move(wake))) {}
Engine::~Engine() = default;
void Engine::Execute(Json const& request, std::string const& connectionId, Reply reply, Push push)
{
    try
    {
        state_->Execute(request, connectionId, reply, std::move(push));
    }
    catch (std::exception const& error)
    {
        reply(Failure(ErrorCode::InvalidRequest, error.what()));
    }
}
void Engine::Tick() { state_->Tick(); }
void Engine::Disconnect(std::string const& connectionId) { state_->Disconnect(connectionId); }
void Engine::Shutdown(std::function<void(ShutdownEnd end)> report) { state_->Shutdown(std::move(report)); }
void Engine::PauseSession(bool paused, std::function<void(Outcome)> completion)
{
    if (auto refusal = state_->Refusal())
    {
        completion({*refusal});
        return;
    }
    state_->PauseSession(paused, std::move(completion));
}
void Engine::TurnOff(std::vector<Setting> const& switches, std::function<void(Outcome)> completion)
{
    if (auto refusal = state_->Refusal())
    {
        completion({*refusal});
        return;
    }
    state_->TurnOff(switches, std::move(completion));
}
void Engine::RetryAll(std::function<void(Outcome)> completion)
{
    if (auto refusal = state_->Refusal())
    {
        completion({*refusal});
        return;
    }
    std::vector<std::string> ids;
    for (auto const& [id, torrent] : state_->torrents)
    {
        if (state_->OffersRetry(torrent))
            ids.push_back(id);
    }
    state_->Retry(ids, std::move(completion));
}
void Engine::RecordPrograms(std::vector<std::string> programs, std::function<void(Outcome)> completion)
{
    if (auto refusal = state_->Refusal())
    {
        completion({*refusal});
        return;
    }
    state_->RecordPrograms(std::move(programs), std::move(completion));
}
void Engine::Add(std::string const& source, std::function<void(Outcome, Added)> completion)
{
    if (auto refusal = state_->Refusal())
    {
        completion({*refusal}, {});
        return;
    }
    state_->AddSource(source, std::move(completion));
}
Activity Engine::Activity() const { return state_->Activity(); }
std::vector<Toast> Engine::TakeToasts()
{
    auto toasts = std::exchange(state_->toasts, {});
    for (auto& [key, toast] : state_->problemChanges)
        toasts.push_back(std::move(toast));
    state_->problemChanges.clear();
    return toasts;
}
std::string Engine::Name(std::string const& torrentId) const
{
    auto found = state_->torrents.find(torrentId);
    return found == state_->torrents.end() ? std::string() : found->second.Name();
}
std::string Engine::Folder(std::string const& torrentId) const
{
    auto found = state_->torrents.find(torrentId);
    return found == state_->torrents.end() ? std::string() : found->second.Folder();
}
std::vector<Toast> Engine::Conditions() const
{
    std::vector<Toast> conditions;
    for (auto const& [key, toast] : state_->problems)
    {
        auto& raised = conditions.emplace_back(toast);
        raised.condition = Toast::Condition{key, ProblemChange::Raised};
    }
    return conditions;
}
std::string Engine::Language() const { return state_->language; }

// A source is no longer than a Windows path, and a NUL would cut a path short
// at the file system.
bool Engine::IsSource(std::string const& source)
{
    return !source.empty() && source.size() <= pathLimit && source.find('\0') == std::string::npos;
}

bool Engine::IsShuttingDown() const { return state_->IsShuttingDown(); }
bool Engine::AwaitsAnswer() const { return !state_->asked.empty(); }
bool Engine::IsLoading() const { return state_->IsLoading(); }
bool Engine::HasStorageFailure() const { return state_->startup == Startup::Failed; }
std::string Engine::StartupError() const { return state_->startupError; }
std::string Engine::SessionId() const { return state_->sessionId; }
bool Engine::ShowsAdd(std::string const& source) const
{
    return IsMagnet(source) ? state_->settings.showsMagnetDialog : state_->settings.showsTorrentDialog;
}
bool Engine::RaisesAdd() const { return state_->settings.raisesAdd; }

bool Engine::HasSettings() const { return state_->startup != Startup::Settings; }
bool Engine::ShowsSplash() const { return state_->settings.showsSplash; }
bool Engine::StartsInTray() const { return state_->settings.startsInTray; }
}
