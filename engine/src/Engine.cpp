#include "Engine.h"
#include "Engine/State.h"
#include <Windows.h>
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

Json Success(Json data)
{
    return {{"ok", true}, {"data", std::move(data)}};
}

namespace
{
char const* ToString(ErrorCode code)
{
    switch (code)
    {
    case ErrorCode::InvalidRequest: return "invalid_request";
    case ErrorCode::UnknownCommand: return "unknown_command";
    case ErrorCode::InvalidSource: return "invalid_source";
    case ErrorCode::InvalidSources: return "invalid_sources";
    case ErrorCode::InvalidDestination: return "invalid_destination";
    case ErrorCode::InvalidPriorities: return "invalid_priorities";
    case ErrorCode::InvalidTrackers: return "invalid_trackers";
    case ErrorCode::InvalidTargets: return "invalid_targets";
    case ErrorCode::ResponseTooLarge: return "response_too_large";
    case ErrorCode::UiConnected: return "ui_connected";
    case ErrorCode::Starting: return "starting";
    case ErrorCode::Stopping: return "stopping";
    case ErrorCode::Unavailable: return "unavailable";
    case ErrorCode::Overloaded: return "overloaded";
    case ErrorCode::StorageFailed: return "storage_failed";
    case ErrorCode::RecoveryRequired: return "recovery_required";
    case ErrorCode::FilesBusy: return "files_busy";
    case ErrorCode::SharedFiles: return "shared_files";
    case ErrorCode::DestinationConflict: return "destination_conflict";
    case ErrorCode::DestinationInUse: return "destination_in_use";
    case ErrorCode::MetadataUnavailable: return "metadata_unavailable";
    case ErrorCode::PreviewExpired: return "preview_expired";
    case ErrorCode::PreviewFailed: return "preview_failed";
    case ErrorCode::TorrentRemoved: return "torrent_removed";
    case ErrorCode::AddFailed: return "add_failed";
    case ErrorCode::RegistrationFailed: return "registration_failed";
    }
    return "";
}

Json Error(char const* code, std::string detail)
{
    return {{"ok", false}, {"error", {{"code", code}, {"detail", std::move(detail)}}}};
}
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
    case NoticeKind::Background: return "background";
    case NoticeKind::Aggregate: return "aggregate";
    }
    return "";
}


Engine::Engine(std::filesystem::path directory, std::function<void()> wake) :
    state_(std::make_unique<State>(std::move(directory), std::move(wake))) {}
Engine::~Engine() = default;
void Engine::Execute(Json const& request, std::string const& connection, Reply reply)
{
    try
    {
        state_->Execute(request, connection, reply);
    }
    catch (std::exception const& error)
    {
        reply(Failure(ErrorCode::InvalidRequest, error.what()));
    }
}
void Engine::Tick() { state_->Tick(); }
void Engine::Disconnect(std::string const& connection) { state_->Disconnect(connection); }
void Engine::Shutdown(std::function<void(std::optional<std::string> failure)> completion) { state_->Shutdown(std::move(completion)); }
void Engine::PauseSession(bool paused, std::function<void(Outcome)> done)
{
    if (auto refusal = state_->Refusal())
    {
        done({*refusal});
        return;
    }
    state_->PauseSession(paused, std::move(done));
}
void Engine::RecordBackgroundNotice(std::function<void(Outcome)> done)
{
    if (auto refusal = state_->Refusal())
    {
        done({*refusal});
        return;
    }
    state_->RecordBackgroundNotice(std::move(done));
}
void Engine::Add(std::string const& source, std::function<void(Outcome, Added)> done)
{
    if (auto refusal = state_->Refusal())
    {
        done({*refusal}, {});
        return;
    }
    state_->AddSource(source, std::move(done));
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

bool Engine::IsStopping() const { return state_->stopping; }
bool Engine::IsLoading() const { return state_->startup != Startup::Ready; }
bool Engine::HasStorageFailure() const { return !state_->startupError.empty(); }
std::string Engine::StartupError() const { return state_->startupError; }
std::string Engine::SessionId() const { return state_->sessionId; }
bool Engine::ShowsAdd() const { return state_->settings.showsAdd; }

bool Engine::HasSettings() const { return state_->startup != Startup::Settings; }
bool Engine::ShowsSplash() const { return state_->settings.showsSplash; }
bool Engine::StartsInTray() const { return state_->settings.startsInTray; }
}
