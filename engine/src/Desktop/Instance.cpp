#include "Desktop/Instance.h"
#include "Desktop/Notices.h"
#include "Desktop/Popup.h"
#include <chrono>
#include <future>
#include <system_error>
#include <tlhelp32.h>

namespace tt::desktop
{
namespace
{
// How often the prompt checks whether the other engine has ended, in
// milliseconds.
constexpr UINT checkInterval = 200;

// Ends `process` unless it has already ended; false when Windows refuses.
bool Terminate(HANDLE process)
{
    if (WaitForSingleObject(process, 0) == WAIT_OBJECT_0)
    {
        return true;
    }
    OwnedHandle target(OpenProcess(PROCESS_TERMINATE, FALSE, GetProcessId(process)));
    return target && TerminateProcess(target.get(), 1);
}

// Ends `other` and each product window from its program folder in this
// Windows session at once, without their final save. It tries every one and
// returns false when Windows refuses any of it.
bool ForceExit(Forwarded const& other)
{
    auto snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE)
    {
        return false;
    }
    OwnedHandle processes(snapshot);
    auto executable = other.engine->parent_path() / TT_WINDOW_FILE;
    DWORD session = 0;
    if (!ProcessIdToSessionId(GetCurrentProcessId(), &session))
    {
        return false;
    }
    std::vector<OwnedHandle> windows;
    PROCESSENTRY32W entry{sizeof(entry)};
    for (bool found = Process32FirstW(processes.get(), &entry); found;
        found = Process32NextW(processes.get(), &entry))
    {
        DWORD owner = 0;
        if (_wcsicmp(entry.szExeFile, TT_WINDOW_FILE) != 0 ||
            !ProcessIdToSessionId(entry.th32ProcessID, &owner) || owner != session)
        {
            continue;
        }
        OwnedHandle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE,
            FALSE, entry.th32ProcessID));
        if (!process)
        {
            continue;
        }
        auto image = ProcessImage(process.get());
        if (image && _wcsicmp(image->c_str(), executable.c_str()) == 0)
        {
            windows.push_back(std::move(process));
        }
    }
    auto listed = GetLastError() == ERROR_NO_MORE_FILES;
    auto ended = Terminate(other.process.get());
    for (auto const& window : windows)
    {
        if (!Terminate(window.get()))
        {
            ended = false;
        }
    }
    return listed && ended;
}

// Whether a process holds the mutex `name`. Throws std::system_error when
// Windows cannot tell.
bool Held(std::wstring const& name)
{
    OwnedHandle mutex(OpenMutexW(SYNCHRONIZE | MUTEX_MODIFY_STATE, FALSE, name.c_str()));
    if (!mutex)
    {
        auto error = GetLastError();
        if (error == ERROR_FILE_NOT_FOUND)
        {
            return false;
        }
        throw std::system_error(static_cast<int>(error), std::system_category(),
            "Cannot check instance ownership");
    }
    auto acquired = WaitForSingleObject(mutex.get(), 0);
    if (acquired == WAIT_OBJECT_0 || acquired == WAIT_ABANDONED)
    {
        ReleaseMutex(mutex.get());
        return false;
    }
    if (acquired == WAIT_TIMEOUT)
    {
        return true;
    }
    throw std::system_error(static_cast<int>(GetLastError()), std::system_category(),
        "Cannot check instance ownership");
}

// The prompt that asks whether to exit the other engine, and what its
// answers do.
class Conflict
{
public:
    Conflict(Strings const& strings, std::wstring const& sid, Forwarded const& other)
        : strings_(strings), sid_(sid), other_(other), popup_(strings)
    {
    }

    bool Resolve();

private:
    void Ask();
    void Exit();
    void Force();
    void Check();
    Strings const& strings_;
    std::wstring const& sid_;
    Forwarded const& other_;
    Recovery state_ = Recovery::Asking;
    // Declared before the popup, so the popup has closed by the time this
    // waits for a pending Exit.
    std::future<Forwarded> request_;
    Popup popup_;
    bool ended_ = false;
};

bool Conflict::Resolve()
{
    Ask();
    // A thread timer, so Check never runs inside a window procedure, which an
    // exception must not cross.
    auto timer = SetTimer(nullptr, 0, checkInterval, nullptr);
    if (!timer)
    {
        throw std::system_error(static_cast<int>(GetLastError()), std::system_category(), "SetTimer");
    }
    MSG message{};
    while (popup_.IsOpen() && GetMessageW(&message, nullptr, 0, 0) > 0)
    {
        if (!message.hwnd && message.message == WM_TIMER && message.wParam == timer)
        {
            Check();
            continue;
        }
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
    KillTimer(nullptr, timer);
    return ended_;
}

void Conflict::Ask()
{
    char const* reason = nullptr;
    switch (state_)
    {
    case Recovery::Asking:
        reason = "reason";
        break;
    // Waiting shows only while Exit's request or Force exit is in progress.
    case Recovery::Exiting:
    case Recovery::Forcing:
        reason = "waiting";
        break;
    case Recovery::Accepted:
        reason = "running";
        break;
    case Recovery::Refused:
        reason = "refused";
        break;
    case Recovery::ForceFailed:
        reason = "failed";
        break;
    }
    // Lucide's power glyph, as the tray's Exit shows. A pending request
    // disables it, because a second one would wait behind the first.
    Popup::Action exit{.glyph = L'\uE140', .label = strings_.Text("tray", "exit"),
        .tooltip = strings_.Text("conflict", "exit_tip"), .run = [this] { Exit(); }, .closes = false,
        .enabled = !request_.valid(), .automationId = L"Exit"};
    // Both answers leave the prompt open until the other engine has ended.
    auto force = Notices::ForceExit(strings_, [this] { Force(); });
    force.closes = false;
    popup_.Ask({.title = strings_.Text("conflict", "title"), .body = strings_.Text("conflict", reason),
        .location = Popup::Location{strings_.Text("conflict", "location"), *other_.engine},
        .answer = std::move(exit), .others = {std::move(force)}});
}

// The other engine may take seconds to answer, so the request runs on its own
// thread, and it goes only to that engine.
void Conflict::Exit()
{
    try
    {
        request_ = std::async(std::launch::async, [sid = sid_, process = GetProcessId(other_.process.get())]
        {
            return Pipe::Forward(sid, {{"command", "exit"}}, std::nullopt, process);
        });
        state_ = Recovery::Exiting;
    }
    catch (std::system_error const&)
    {
        state_ = Recovery::Refused;
    }
    Ask();
}

void Conflict::Force()
{
    state_ = ForceExit(other_) ? Recovery::Forcing : Recovery::ForceFailed;
    Ask();
}

void Conflict::Check()
{
    // Another launch that waited too may already hold the engine lock and have
    // opened its own window. The wait then ends, so Force exit never reaches
    // that window.
    if (WaitForSingleObject(other_.process.get(), 0) == WAIT_OBJECT_0 &&
        (!HasWindow(sid_) || Held(EngineLockName(sid_))))
    {
        ended_ = true;
        popup_.Close();
        return;
    }
    if (!request_.valid() || request_.wait_for(std::chrono::seconds(0)) != std::future_status::ready)
    {
        return;
    }
    auto accepted = false;
    try
    {
        accepted = request_.get().outcome == Forwarding::Accepted;
    }
    catch (std::exception const&)
    {
        // A request that failed was not accepted.
    }
    // A result that arrives after Force exit does not change that state.
    if (state_ == Recovery::Exiting)
    {
        state_ = accepted ? Recovery::Accepted : Recovery::Refused;
    }
    // Exit becomes available again, also after an accepted request, because
    // the other TinyTorrent may ask its own confirmation, which the person
    // can cancel.
    Ask();
}
}

std::wstring EngineLockName(std::wstring const& sid)
{
    return L"Local\\TinyTorrent.Engine." + sid;
}

bool HasWindow(std::wstring const& sid)
{
    return Held(L"Local\\TinyTorrent.Window." + sid);
}

bool ExitOther(Strings const& strings, std::wstring const& sid, Forwarded const& other)
{
    return Conflict(strings, sid, other).Resolve();
}
}
