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
    {"recovery_required", ErrorCode::RecoveryRequired},
    {"files_busy", ErrorCode::FilesBusy},
    {"shared_files", ErrorCode::SharedFiles},
    {"destination_conflict", ErrorCode::DestinationConflict},
    {"destination_in_use", ErrorCode::DestinationInUse},
    {"move_interrupted", ErrorCode::MoveInterrupted},
    {"metadata_unavailable", ErrorCode::MetadataUnavailable},
    {"preview_expired", ErrorCode::PreviewExpired},
    {"preview_failed", ErrorCode::PreviewFailed},
    {"torrent_removed", ErrorCode::TorrentRemoved},
    {"alias_conflict", ErrorCode::AliasConflict},
    {"add_failed", ErrorCode::AddFailed},
    {"registration_failed", ErrorCode::RegistrationFailed}};

Json Error(char const* code, std::string detail)
{
    if (std::string_view(code) == ToString(ErrorCode::StorageFailed) && detail == "storage_overloaded")
    {
        code = ToString(ErrorCode::Overloaded);
        detail.clear();
    }
    return {{"ok", false}, {"error", {{"code", code}, {"detail", std::move(detail)}}}};
}
}

char const* ToString(ErrorCode code)
{
    auto word = Word(errors, code);
    return word.empty() ? "" : word.data();
}

std::optional<ErrorCode> ParseError(std::string_view word)
{
    return Parse(errors, word);
}

Json Failure(ErrorCode code, std::string detail)
{
    return Error(ToString(code), std::move(detail));
}

Json Failure(ProblemKind kind, std::string detail)
{
    return Error(ToString(kind), std::move(detail));
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

char const* ToString(NoticeKind kind)
{
    switch (kind)
    {
    case NoticeKind::Completed: return "completed";
    case NoticeKind::Error: return "error";
    case NoticeKind::Added: return "added";
    case NoticeKind::Duplicate: return "duplicate";
    case NoticeKind::AddFailed: return "add_failed";
    case NoticeKind::DeleteFailed: return "delete_failed";
    case NoticeKind::Failure: return "failure";
    case NoticeKind::Background: return "background";
    case NoticeKind::MissingProgram: return "missing_program";
    case NoticeKind::Aggregate: return "aggregate";
    }
    return "";
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
void Engine::Shutdown(std::function<void(std::optional<std::string> failure)> completion) { state_->Shutdown(std::move(completion)); }
void Engine::PauseSession(bool paused, std::function<void(Outcome)> completion)
{
    if (auto refusal = state_->Refusal())
    {
        completion({*refusal});
        return;
    }
    state_->PauseSession(paused, std::move(completion));
}
void Engine::SetBackgroundNotification(bool enabled, std::function<void(Outcome)> completion)
{
    if (auto refusal = state_->Refusal())
    {
        completion({*refusal});
        return;
    }
    state_->Configure(Json{{"notify_background", enabled}}, std::move(completion));
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
Json Engine::Snapshot() const { return state_->Snapshot(); }
Activity Engine::Activity() const { return state_->Activity(); }
std::vector<Notice> Engine::TakeNotices() { return std::exchange(state_->notices, {}); }
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
std::string Engine::Language() const { return state_->language; }
std::string Engine::Theme() const { return state_->settings.theme; }

// Windows paths are at most 32,767 characters, and a NUL would cut a path
// short at the file system.
bool Engine::IsSource(std::string const& source)
{
    return !source.empty() && source.size() <= 32'768 && source.find('\0') == std::string::npos;
}

bool Engine::IsShuttingDown() const { return state_->shuttingDown; }
bool Engine::IsLoading() const { return state_->startup != Startup::Ready; }
bool Engine::HasStorageFailure() const { return !state_->startupError.empty(); }
std::string Engine::StartupError() const { return state_->startupError; }
std::string Engine::SessionId() const { return state_->sessionId; }
bool Engine::ShowsAdd() const { return state_->settings.showsAdd; }
bool Engine::RaisesAdd() const { return state_->settings.raisesAdd; }

bool Engine::HasSettings() const { return state_->startup != Startup::Settings; }
bool Engine::ShowsSplash() const { return state_->settings.showsSplash; }
bool Engine::StartsInTray() const { return state_->settings.startsInTray; }
}
