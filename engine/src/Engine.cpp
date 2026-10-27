#include "Engine.h"
#include "Engine/State.h"
#include <Windows.h>
#include <wincrypt.h>
#include <system_error>
#include <utility>

namespace tt
{
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

bool SameFolder(std::filesystem::path const& first, std::filesystem::path const& second)
{
    std::error_code error;
    return first == second || std::filesystem::equivalent(first, second, error);
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

std::wstring Executable()
{
    // Windows paths are at most 32,767 characters.
    std::wstring path(32'768, L'\0');
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
    std::wstring path(32'768, L'\0');
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
    }
    return "";
}

std::string ToString(ToastSwitch toasts)
{
    for (auto const& [name, value] : toastSwitches)
    {
        if (value == toasts)
        {
            return std::string(name);
        }
    }
    return {};
}

std::vector<ToastSwitch> AllToastSwitches()
{
    std::vector<ToastSwitch> toasts;
    for (auto const& [name, value] : toastSwitches)
    {
        toasts.push_back(value);
    }
    return toasts;
}


Engine::Engine(std::filesystem::path directory, std::function<void()> wake) :
    state_(std::make_unique<State>(std::move(directory), std::move(wake))) {}
Engine::~Engine() = default;
void Engine::Execute(Json const& request, std::string const& connectionId, Reply reply)
{
    try
    {
        state_->Execute(request, connectionId, reply);
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
void Engine::TurnOff(std::vector<ToastSwitch> const& toasts, std::function<void(Outcome)> completion)
{
    if (auto refusal = state_->Refusal())
    {
        completion({*refusal});
        return;
    }
    state_->TurnOff(toasts, std::move(completion));
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
        if (torrent.NeedsPerson())
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

// Windows paths are at most 32,767 characters, and a NUL would cut a path
// short at the file system.
bool Engine::IsSource(std::string const& source)
{
    return !source.empty() && source.size() <= 32'768 && source.find('\0') == std::string::npos;
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
