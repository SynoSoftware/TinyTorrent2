#include "Engine.h"
#include "Engine/State.h"
#include <Windows.h>
#include <utility>

namespace tiny
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

Json Failure(std::string code, std::string detail)
{
    return {{"ok", false}, {"error", {{"code", std::move(code)}, {"detail", std::move(detail)}}}};
}

std::wstring Executable()
{
    // Windows paths are at most 32,767 characters.
    wchar_t path[32'768];
    auto size = GetModuleFileNameW(nullptr, path, static_cast<DWORD>(std::size(path)));
    if (!size || size == std::size(path))
    {
        throw std::runtime_error("Cannot locate the engine executable.");
    }
    return std::wstring(path, size);
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
void Engine::Execute(Json const& request, std::function<void(Json)> reply)
{
    try
    {
        state_->Execute(request, reply);
    }
    catch (std::exception const& error)
    {
        reply(Failure("invalid_request", error.what()));
    }
}
void Engine::Tick() { state_->Tick(); }
void Engine::Disconnect(std::string const& connection) { state_->Disconnect(connection); }
void Engine::Shutdown(std::function<void(std::optional<std::string> failure)> completion) { state_->Shutdown(std::move(completion)); }
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

// Windows paths are at most 32,767 characters, and a NUL would cut a path
// short at the file system.
bool Engine::IsSource(std::string const& source)
{
    return !source.empty() && source.size() <= 32'768 && source.find('\0') == std::string::npos;
}

bool Engine::IsStopping() const { return state_->stopping; }
bool Engine::IsLoading() const { return state_->loading; }
bool Engine::HasStorageFailure() const { return !state_->startupError.empty(); }
bool Engine::ShowsAdd() const { return state_->settings.showsAdd; }
std::string Engine::DefaultDestination() const { return state_->settings.destination; }
}
