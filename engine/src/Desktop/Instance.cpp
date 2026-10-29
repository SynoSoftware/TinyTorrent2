#include "Desktop/Instance.h"
#include "Registration.h"
#include "Desktop/Window.h"
#include "Desktop/Toasts.h"
#include "Desktop/Popup.h"
#include <algorithm>
#include <chrono>
#include <future>
#include <shellapi.h>
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

// The name of the engine lock, the mutex that the running engine of the logon
// session `sid` holds.
std::wstring EngineLockName(std::wstring const& sid)
{
    return L"Local\\TinyTorrent.Engine." + sid;
}

// Whether a product window of the logon session `sid` runs, including one
// that outlived its engine. Throws std::system_error when Windows cannot tell.
bool HasWindow(std::wstring const& sid)
{
    return Held(L"Local\\TinyTorrent.Window." + sid);
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

    // True once the other engine has ended and either its window has ended
    // too or another engine holds the engine lock; false when the person
    // cancels.
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
    auto force = Toasts::ForceExit(strings_, [this] { Force(); });
    force.closes = false;
    Popup::Prompt prompt(strings_.Text("conflict", "title"), strings_.Text("conflict", reason), std::move(exit));
    prompt.location = Popup::Location{strings_.Text("conflict", "location"), *other_.engine};
    prompt.others = {std::move(force)};
    popup_.Ask(std::move(prompt));
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

// Another version keeps this launch out until that engine closes, and so does
// another data folder for a headless launch, which cannot ask to exit it. Its
// folder identifies it, and Retry starts this launch again once it has closed.
Refusal OtherEngine(Forwarded const& forwarded)
{
    Refusal refusal{.key = forwarded.outcome == Forwarding::OtherVersion ? "version" : "other_store", .retry = true};
    // The detail line shortens a path in its middle, so the folder rather than
    // the program path keeps the folder that tells engines apart in view.
    if (forwarded.engine)
    {
        refusal.folder = forwarded.engine->parent_path();
        refusal.detail = refusal.folder->wstring();
    }
    return refusal;
}

// Hands the launch's request to the running engine. A launch that only starts
// the engine in the background asks nothing of a running one.
Forwarded Forward(std::wstring const& sid, Arguments const& arguments)
{
    auto const& store = arguments.directory;
    if (std::holds_alternative<Arguments::Exit>(arguments.request))
    {
        return Pipe::Forward(sid, {{"command", "exit"}});
    }
    if (auto request = std::get_if<Arguments::Register>(&arguments.request))
    {
        auto action = std::ranges::find(registrationActions, request->action,
            &std::pair<std::string_view, RegistrationAction>::second);
        // An engine of another store, such as a test's, would register its
        // own program with Windows.
        return Pipe::Forward(sid, {{"command", "registration"}, {"action", std::string(action->first)}}, store);
    }
    auto const& open = std::get<Arguments::Open>(arguments.request);
    if (!open.sources.empty())
    {
        return Pipe::Forward(sid, {{"command", "activate_sources"}, {"sources", open.sources}}, store);
    }
#ifdef _DEBUG
    // The running engine shows the sample, as it takes another launch's
    // sources.
    if (open.sample)
    {
        return Pipe::Forward(sid, {{"command", "sample_toast"}});
    }
#endif
    if (!open.background && !arguments.headless)
    {
        return Pipe::Forward(sid, {{"command", "open"}}, store);
    }
    return {Forwarding::Accepted};
}
}

Arguments Arguments::Read(wchar_t const* line)
{
    int count = 0;
    auto values = CommandLineToArgvW(line, &count);
    // The first value names the program.
    std::vector<std::wstring> words(values + (count ? 1 : 0), values + count);
    LocalFree(values);
    Arguments result;
    auto refuse = [&result](char const* key)
    {
        result.request = Refusal{.key = key};
        return result;
    };
    // The first option with a missing or unknown value. It is refused once
    // the whole line is read, so a `--headless` after it still applies.
    char const* invalid = nullptr;
    auto note = [&invalid](char const* key)
    {
        invalid = invalid ? invalid : key;
    };
    bool literal = false;
    bool background = false;
    bool exiting = false;
#ifdef _DEBUG
    bool sample = false;
#endif
    std::optional<RegistrationAction> registration;
    std::vector<std::string> sources;
    std::vector<std::wstring> classes;
    for (std::size_t index = 0; index < words.size(); ++index)
    {
        auto const& word = words[index];
        if (!literal && word == option::literal)
        {
            literal = true;
        }
        else if (!literal && word == option::headless)
        {
            result.headless = true;
        }
        else if (!literal && word == option::background)
        {
            background = true;
        }
        else if (!literal && word == option::exit)
        {
            exiting = true;
        }
        else if (!literal && word == option::registration)
        {
            registration = ++index < words.size() ? Parse(registrationActions, Utf8(words[index])) : std::nullopt;
            if (!registration)
            {
                note("argument_registration");
            }
        }
        else if (!literal && word == option::data)
        {
            if (index + 1 == words.size() || words[index + 1] == option::literal)
            {
                note("argument_folder");
            }
            else
            {
                result.directory = words[++index];
            }
        }
#ifdef _DEBUG
        else if (!literal && word == option::toast)
        {
            sample = true;
            background = true;
        }
#endif
        else if (!literal && word == option::repairClass)
        {
            if (++index == words.size())
            {
                note("argument_class");
            }
            else
            {
                classes.push_back(words[index]);
            }
        }
        else
        {
            auto source = Utf8(word);
            NormaliseMagnet(source);
            if (!word.empty() && !IsMagnet(source))
            {
                source = Utf8(std::filesystem::absolute(std::filesystem::path(word)).wstring());
            }
            sources.push_back(std::move(source));
        }
    }
    if (invalid)
    {
        return refuse(invalid);
    }
    if (registration && !sources.empty())
    {
        return refuse("argument_sources");
    }
    if (exiting && (registration || !sources.empty() || background || !result.directory.empty()))
    {
        return refuse("argument_exit");
    }
    if (!sources.empty() && !ValidSources(sources))
    {
        return refuse("sources_limit");
    }
    if (!classes.empty())
    {
        if (registration || !sources.empty() || exiting || background || !result.directory.empty())
        {
            return refuse("argument_repair");
        }
        result.request = Repair{std::move(classes)};
    }
    else if (exiting)
    {
        result.request = Exit{};
    }
    else if (registration)
    {
        result.request = Register{*registration};
    }
    else
    {
        Open open{.background = background, .sources = std::move(sources)};
#ifdef _DEBUG
        open.sample = sample;
#endif
        result.request = std::move(open);
    }
    return result;
}

Ownership Claim(std::wstring const& sid, Arguments const& arguments)
{
    OwnedHandle lock;
    {
        Security security(sid);
        lock.reset(CreateMutexW(&security.Attributes(), FALSE, EngineLockName(sid).c_str()));
    }
    if (!lock)
    {
        throw std::runtime_error("Cannot claim engine instance ownership.");
    }
    auto exiting = std::holds_alternative<Arguments::Exit>(arguments.request);
    auto acquired = WaitForSingleObject(lock.get(), 0);
    while (acquired == WAIT_TIMEOUT)
    {
        auto forwarded = Forward(sid, arguments);
        if (forwarded.outcome == Forwarding::OtherStore && !arguments.headless)
        {
            // Exit and Force exit need the identified process and program.
            if (!forwarded.process || !forwarded.engine)
            {
                throw Refusal{.key = "other_store_unidentified", .retry = true};
            }
            if (!Conflict(Strings(), sid, forwarded).Resolve())
            {
                return {.code = 1};
            }
            acquired = WaitForSingleObject(lock.get(), 0);
            continue;
        }
        if (forwarded.outcome == Forwarding::OtherVersion || forwarded.outcome == Forwarding::OtherStore)
        {
            throw OtherEngine(forwarded);
        }
        if (!exiting)
        {
            if (forwarded.outcome == Forwarding::Refused)
            {
                throw Refusal{.key = "activation_refused", .retry = true};
            }
            return {};
        }
        acquired = WaitForSingleObject(lock.get(), 30'000);
        if (acquired != WAIT_OBJECT_0 && acquired != WAIT_ABANDONED)
        {
            throw Refusal{.key = "engine_running", .retry = true};
        }
    }
    if (acquired != WAIT_OBJECT_0 && acquired != WAIT_ABANDONED)
    {
        throw std::runtime_error("Cannot acquire engine instance ownership.");
    }
    if (exiting)
    {
        if (HasWindow(sid))
        {
            throw Refusal{.key = "window_open", .retry = true};
        }
        ReleaseMutex(lock.get());
        return {};
    }
    return {.lock = std::move(lock)};
}
}
