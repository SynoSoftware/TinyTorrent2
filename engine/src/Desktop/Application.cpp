#include "Desktop/Application.h"
#include "Registration.h"
#include "Resources.h"
#include <algorithm>
#include <commctrl.h>
#include <shellapi.h>
#include <shobjidl.h>
#include <propkey.h>
#include <propvarutil.h>
#include <stdexcept>

namespace tt::desktop
{
namespace
{
constexpr UINT dispatch = WM_APP + 1;
constexpr UINT tray = WM_APP + 2;
constexpr UINT wake = WM_APP + 3;
constexpr UINT backgroundOff = WM_APP + 4;
constexpr UINT_PTR tickTimer = 1;
constexpr UINT_PTR trayTimer = 2;

constexpr std::pair<std::string_view, Command> commands[] = {
    {"registration", Command::Registration},
    {"ready", Command::Ready},
    {"window_closed", Command::WindowClosed},
    {"activate_reply", Command::ActivateReply},
    {"close_reply", Command::CloseReply},
    {"exit_reply", Command::ExitReply},
    {"activate_sources", Command::ActivateSources},
    {"pending_activations", Command::PendingActivations},
    {"activations_received", Command::ActivationsReceived},
    {"open", Command::Open},
    {"exit", Command::Exit}};

constexpr std::pair<std::string_view, CloseState> closeStates[] = {
    {"waiting", CloseState::Waiting},
    {"cancelled", CloseState::Cancelled},
    {"closing", CloseState::Closing}};

constexpr std::pair<std::string_view, ExitAnswer> exitAnswers[] = {
    {"confirmed", ExitAnswer::Confirmed},
    {"cancelled", ExitAnswer::Cancelled},
    {"unavailable", ExitAnswer::Unavailable}};

// Durations in milliseconds, as GetTickCount64 counts.
constexpr UINT tickInterval = 1000;
// How long the window may take to open or to close before reporting a failure.
constexpr ULONGLONG windowTimeout = 30'000;
// How long session end waits for the final save before the process ends.
constexpr ULONGLONG sessionEndTimeout = 4000;

// Pipe requests that wait for this thread; more are refused as overloaded.
constexpr std::size_t requestLimit = 128;
// The most sources that wait to be added at one time.
constexpr std::size_t sourceLimit = 256;

// WinUI 3 gives its top-level windows this class.
constexpr wchar_t winUiWindowClass[] = L"WinUIDesktopWin32WindowClass";

LRESULT CALLBACK Broadcast(HWND window, UINT message, WPARAM first, LPARAM second)
{
    if (message == WM_NCCREATE)
    {
        auto owner = reinterpret_cast<CREATESTRUCTW*>(second)->lpCreateParams;
        SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(owner));
    }
    auto owner = reinterpret_cast<HWND>(GetWindowLongPtrW(window, GWLP_USERDATA));
    bool forwarded = message == WM_ENDSESSION || message == WM_POWERBROADCAST || message == WM_MEASUREITEM ||
        message == WM_DRAWITEM;
    if (owner && forwarded)
    {
        return SendMessageW(owner, message, first, second);
    }
    if (message == WM_QUERYENDSESSION)
    {
        return TRUE;
    }
    // Registered messages, such as Explorer's TaskbarCreated.
    if (owner && message >= 0xC000)
    {
        PostMessageW(owner, message, first, second);
    }
    return DefWindowProcW(window, message, first, second);
}

// The window from the same build, which is always beside the engine.
std::filesystem::path WindowExecutable()
{
    return std::filesystem::path(Executable()).parent_path() / TT_WINDOW_FILE;
}

HWND FindWinUiWindow(DWORD processId)
{
    struct Target
    {
        DWORD processId;
        HWND window = nullptr;
    } target{processId};
    EnumWindows([](HWND window, LPARAM parameter) -> BOOL
    {
        auto& target = *reinterpret_cast<Target*>(parameter);
        DWORD processId = 0;
        GetWindowThreadProcessId(window, &processId);
        if (processId != target.processId)
        {
            return TRUE;
        }
        wchar_t name[128]{};
        GetClassNameW(window, name, static_cast<int>(std::size(name)));
        if (wcscmp(name, winUiWindowClass) != 0)
        {
            return TRUE;
        }
        target.window = window;
        return FALSE;
    }, reinterpret_cast<LPARAM>(&target));
    return target.window;
}

// Makes the taskbar relaunch the engine, under the product's name and icon,
// when the person pins the window of `processId`.
void SetRelaunch(DWORD processId)
{
    auto window = FindWinUiWindow(processId);
    if (!window)
    {
        return;
    }
    auto executable = Executable();
    auto initialized = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    IPropertyStore* properties = nullptr;
    auto outcome = SHGetPropertyStoreForWindow(window, IID_PPV_ARGS(&properties));
    if (SUCCEEDED(outcome))
    {
        auto set = [properties, &outcome](PROPERTYKEY const& key, std::wstring const& value)
        {
            if (FAILED(outcome))
            {
                return;
            }
            PROPVARIANT property{};
            outcome = InitPropVariantFromString(value.c_str(), &property);
            if (SUCCEEDED(outcome))
            {
                outcome = properties->SetValue(key, property);
            }
            PropVariantClear(&property);
        };
        set(PKEY_AppUserModel_RelaunchCommand, L"\"" + executable + L"\"");
        set(PKEY_AppUserModel_RelaunchDisplayNameResource,
            L"@" + executable + L",-" + std::to_wstring(IDS_PRODUCT_NAME));
        set(PKEY_AppUserModel_RelaunchIconResource, executable + L",-" + std::to_wstring(IDI_TINYTORRENT));
        set(PKEY_AppUserModel_ID, appId);
        if (SUCCEEDED(outcome))
        {
            outcome = properties->Commit();
        }
        properties->Release();
    }
    if (FAILED(outcome))
    {
        OutputDebugStringW(L"TinyTorrent could not set taskbar relaunch properties.\n");
    }
    if (SUCCEEDED(initialized))
    {
        CoUninitialize();
    }
}
}

Application::Application(std::filesystem::path directory, std::wstring sid, bool headless)
    : headless_(headless),
      splash_(strings_),
      power_(strings_)
{
    auto module = GetModuleHandleW(nullptr);
    std::filesystem::create_directories(directory);
    directory = std::filesystem::canonical(directory);
    auto file = CreateFileW((directory / L"ownership.lock").c_str(), GENERIC_READ | GENERIC_WRITE,
        0, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        throw std::runtime_error(Utf8(strings_.Text("error", "ownership")) + " " + Utf8(directory.wstring()));
    }
    ownership_.reset(file);
    WNDCLASSW type{};
    type.lpfnWndProc = Procedure;
    type.hInstance = module;
    type.hIcon = LoadIconW(module, MAKEINTRESOURCEW(IDI_TINYTORRENT));
    type.lpszClassName = L"TinyTorrent.Owner";
    RegisterClassW(&type);
    // Procedure stores the handle in owner_ while the window is created,
    // because its first messages already need it.
    CreateWindowW(type.lpszClassName, productName, 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, module, this);
    if (!owner_)
    {
        throw std::runtime_error("Cannot create the engine owner window.");
    }
    type.lpfnWndProc = Broadcast;
    // Message-only windows do not receive Explorer's restart broadcast.
    type.lpszClassName = L"TinyTorrent.Broadcast";
    RegisterClassW(&type);
    broadcast_.reset(CreateWindowExW(WS_EX_TOOLWINDOW, type.lpszClassName, productName, 0, 0, 0, 0, 0,
        nullptr, nullptr, module, owner_.get()));
    if (!broadcast_)
    {
        throw std::runtime_error("Cannot create the broadcast window.");
    }
    tray_ = std::make_unique<Tray>(owner_.get(), tray, backgroundOff, broadcast_.get(), strings_, headless_);
    auto data = directory.wstring();
    engine_ = std::make_unique<Engine>(std::move(directory),
        [this] { PostMessageW(owner_.get(), wake, 0, 0); });
    Json hello{
        {"type", "hello"},
        {"version", Pipe::version},
        {"session_id", engine_->SessionId()},
        {"data_directory", Utf8(data)}};
    {
        Security security(sid);
        pipe_ = std::make_unique<Pipe>(sid, security.Attributes(), std::move(hello),
            [this](std::shared_ptr<Pipe::Connection> client, Json request, Reply reply)
            {
                Dispatch(std::move(client), std::move(request), std::move(reply));
            });
    }
    SetTimer(owner_.get(), tickTimer, tickInterval, nullptr);
    powerNotification_.reset(RegisterPowerSettingNotification(owner_.get(), &GUID_ACDC_POWER_SOURCE,
        DEVICE_NOTIFY_WINDOW_HANDLE));
    // The trailing \. keeps a root folder such as C:\ from escaping the
    // closing quote.
    auto restart = std::wstring(option::background) + L" " + option::data + L" \"" + data + L"\\.\"";
    RegisterApplicationRestart(restart.c_str(), RESTART_NO_PATCH | RESTART_NO_REBOOT);
}

int Application::Run(bool background, std::vector<std::string> sources)
{
    tray_->Add();
    Refresh();
    if (!sources.empty())
    {
        auto response = Activate(std::move(sources));
        if (!response.value("ok", false))
        {
            ShowError("source", Wide(response.at("error").dump()));
            return 1;
        }
    }
    else
    {
        pendingStart_ = !background && !headless_;
    }
    MSG message{};
    while (GetMessageW(&message, nullptr, 0, 0) > 0)
    {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
    return static_cast<int>(message.wParam);
}

LRESULT CALLBACK Application::Procedure(HWND window, UINT message, WPARAM first, LPARAM second)
{
    auto application = reinterpret_cast<Application*>(GetWindowLongPtrW(window, GWLP_USERDATA));
    if (message == WM_NCCREATE)
    {
        application = static_cast<Application*>(reinterpret_cast<CREATESTRUCTW*>(second)->lpCreateParams);
        application->owner_.reset(window);
        SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(application));
    }
    if (!application)
    {
        return DefWindowProcW(window, message, first, second);
    }
    return application->Handle(message, first, second);
}

LRESULT Application::Handle(UINT message, WPARAM first, LPARAM second)
{
    switch (message)
    {
    case dispatch:
    {
        std::deque<std::function<void()>> requests;
        {
            std::lock_guard lock(mutex_);
            requests.swap(requests_);
        }
        for (auto& action : requests)
        {
            action();
        }
        return 0;
    }
    case wake:
        Tick();
        return 0;
    case tray:
        OnTray(first, second);
        return 0;
    case backgroundOff:
        engine_->SetBackgroundNotification(false, [this](Outcome outcome)
        {
            if (outcome.error)
            {
                auto detail = Utf8(strings_.Text("error", "notification")) + " " + outcome.detail;
                Notify({.kind = NoticeKind::Failure, .name = Utf8(productName), .detail = detail});
            }
            Refresh();
        });
        return 0;
    case WM_TIMER:
        if (first == tickTimer)
        {
            OnTimer();
        }
        else if (first == trayTimer)
        {
            KillTimer(owner_.get(), trayTimer);
            if (trayClick_ == TrayClick::Waiting)
            {
                trayClick_ = TrayClick::Idle;
                ShowMenu(trayPoint_);
            }
        }
        return 0;
    case WM_MEASUREITEM:
        return tray_->Measure(*reinterpret_cast<MEASUREITEMSTRUCT*>(second));
    case WM_DRAWITEM:
        return tray_->Draw(*reinterpret_cast<DRAWITEMSTRUCT const*>(second));
    case WM_ENDSESSION:
        if (first)
        {
            EndSession();
        }
        return 0;
    case WM_POWERBROADCAST:
        Tick();
        return TRUE;
    }
    if (message == taskbarCreated_)
    {
        KillTimer(owner_.get(), trayTimer);
        trayClick_ = TrayClick::Idle;
        tray_->Add();
        Refresh();
        return 0;
    }
    return DefWindowProcW(owner_.get(), message, first, second);
}

void Application::OnTray(WPARAM first, LPARAM second)
{
    auto notification = LOWORD(second);
    if (notification == WM_LBUTTONDOWN)
    {
        KillTimer(owner_.get(), trayTimer);
        trayClick_ = TrayClick::Idle;
    }
    else if (notification == NIN_SELECT)
    {
        if (trayClick_ == TrayClick::Double)
        {
            trayClick_ = TrayClick::Idle;
            return;
        }
        trayPoint_ = {static_cast<short>(LOWORD(first)), static_cast<short>(HIWORD(first))};
        trayClick_ = TrayClick::Waiting;
        // The first release may belong to a double-click; do not open a menu under its second click.
        SetTimer(owner_.get(), trayTimer, GetDoubleClickTime(), nullptr);
    }
    else if (notification == WM_LBUTTONDBLCLK)
    {
        KillTimer(owner_.get(), trayTimer);
        trayClick_ = TrayClick::Double;
        Open();
    }
    else if (notification == NIN_KEYSELECT)
    {
        KillTimer(owner_.get(), trayTimer);
        trayClick_ = TrayClick::Idle;
        Open();
    }
    else if (notification == WM_CONTEXTMENU)
    {
        KillTimer(owner_.get(), trayTimer);
        trayClick_ = TrayClick::Idle;
        POINT point{static_cast<short>(LOWORD(first)), static_cast<short>(HIWORD(first))};
        ShowMenu(point);
    }
    else if (notification == NIN_BALLOONUSERCLICK)
    {
        auto shown = tray_->TakeNotice();
        if (!shown)
        {
            return;
        }
        if (shown->kind == NoticeKind::MissingProgram)
        {
            // Set first, so a launch failure inside Open can clear it.
            showsSettings_ = true;
            if (!Open())
            {
                showsSettings_ = false;
            }
            ShowSettings();
            return;
        }
        // A completed torrent opens its folder; any other notice opens the window.
        auto folder = shown->kind == NoticeKind::Completed ? engine_->Folder(shown->torrentId) : std::string();
        if (folder.empty())
        {
            Open();
        }
        else
        {
            ShellExecuteW(nullptr, L"open", Wide(folder).c_str(), nullptr, nullptr, SW_SHOWNORMAL);
        }
    }
    else if (notification == NIN_BALLOONTIMEOUT)
    {
        tray_->TakeNotice();
    }
}

void Application::ShowMenu(POINT point)
{
    Refresh();
    auto item = tray_->ShowMenu(point);
    if (item == TrayItem::Open)
    {
        Open();
    }
    else if (item == TrayItem::Pause)
    {
        Pause();
    }
    else if (item == TrayItem::Exit)
    {
        Exit();
    }
}

void Application::OnTimer()
{
    Tick();
    if (waitingSince_ && process_ && WaitForSingleObject(process_.get(), 0) == WAIT_OBJECT_0 && !windowClient_)
    {
        if (IsExiting())
        {
            Shutdown();
        }
        else
        {
            DWORD code = 0;
            GetExitCodeProcess(process_.get(), &code);
            if (code != 0)
            {
                waitingSince_ = 0;
                reopen_ = false;
                showsSettings_ = false;
                splash_.Close();
                ShowError("startup", std::to_wstring(code));
            }
            else if (reopen_)
            {
                waitingSince_ = 0;
            }
        }
    }
    if (waitingSince_ && GetTickCount64() - waitingSince_ > windowTimeout)
    {
        waitingSince_ = 0;
        reopen_ = false;
        showsSettings_ = false;
        if (!IsExiting() && exit_ != ExitPhase::WindowConfirming)
        {
            splash_.Close();
            ShowError("startup");
        }
        else if (headless_)
        {
            Shutdown();
        }
        else
        {
            CancelExit();
            ShowError("unresponsive");
        }
    }
}

void Application::Dispatch(std::shared_ptr<Pipe::Connection> client, Json request, Reply reply)
{
    std::lock_guard lock(mutex_);
    if (request.is_null() && !client->dispatched)
    {
        return;
    }
    if (requests_.size() >= requestLimit && !request.is_null())
    {
        reply(Failure(ErrorCode::Overloaded));
        return;
    }
    client->dispatched = true;
    requests_.push_back(
        [this, client = std::move(client), request = std::move(request), reply = std::move(reply)]
    {
        Receive(client, request, reply);
    });
    PostMessageW(owner_.get(), dispatch, 0, 0);
}

// Commands about the window and launch sources are answered here; the engine
// answers all others.
void Application::Receive(std::shared_ptr<Pipe::Connection> const& client, Json const& request, Reply const& reply)
{
    if (request.is_null())
    {
        Disconnect(client);
        return;
    }
    auto command = Parse(commands, request.at("command").get<std::string>());
    if (exit_ == ExitPhase::SessionEnding || (command == Command::Registration && engine_->IsShuttingDown()))
    {
        reply(Failure(ErrorCode::ShuttingDown));
        return;
    }
    // Only the window reports that it closed; the engine answers the same
    // command from any other client as unknown.
    if (command == Command::WindowClosed && windowClient_ != client)
    {
        command.reset();
    }
    if (!command)
    {
        engine_->Execute(request, client->connectionId, [this, reply](Json response)
        {
            Refresh();
            reply(std::move(response));
        });
        return;
    }
    // Reading a missing field, or one of the wrong type, throws; like the
    // engine, the host answers that request as invalid.
    try
    {
        switch (*command)
        {
        case Command::Registration:
            reply(registration_.Execute(request.at("action").get<std::string>()));
            break;
        case Command::Ready:
            OnReady(client, reply);
            break;
        case Command::WindowClosed:
            OnClosed(reply);
            break;
        case Command::ActivateReply:
            OnActivateReply(client, request.at("available").get<bool>(), reply);
            break;
        case Command::CloseReply:
        {
            auto state = Parse(closeStates, request.at("state").get<std::string>());
            if (!state)
            {
                reply(Failure(ErrorCode::InvalidRequest));
                break;
            }
            OnCloseReply(client, *state, reply);
            break;
        }
        case Command::ExitReply:
        {
            auto answer = Parse(exitAnswers, request.at("answer").get<std::string>());
            if (!answer)
            {
                reply(Failure(ErrorCode::InvalidRequest));
                break;
            }
            OnExitReply(client, *answer, reply);
            break;
        }
        case Command::ActivateSources:
            reply(Activate(request.at("sources").get<std::vector<std::string>>()));
            break;
        case Command::PendingActivations:
            OnPendingActivations(client, reply);
            break;
        case Command::ActivationsReceived:
            OnActivationsReceived(client,
                request.value("activation_ids", std::vector<std::string>{}), reply);
            break;
        case Command::Open:
            if (!Open())
            {
                reply(Failure(ErrorCode::ShuttingDown));
                break;
            }
            reply(Success());
            break;
        case Command::Exit:
            reply(Success());
            Exit();
            break;
        }
    }
    catch (Json::exception const& error)
    {
        reply(Failure(ErrorCode::InvalidRequest, error.what()));
    }
}

// The window has drawn its first state and waits to appear.
void Application::OnReady(std::shared_ptr<Pipe::Connection> const& client, Reply const& reply)
{
    if (windowClient_)
    {
        reply(Failure(ErrorCode::WindowConnected));
        return;
    }
    windowClient_ = client;
    if (auto processId = client->processId)
    {
        auto actual = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, processId);
        if (actual)
        {
            process_.reset(actual);
        }
        SetRelaunch(processId);
    }
    waitingSince_ = 0;
    reopen_ = false;
    if (launchedAt_)
    {
        splash_.Record(GetTickCount64() - launchedAt_);
        launchedAt_ = 0;
    }
    // The reply lets the drawn window appear, and its activate reply closes
    // the splash.
    splash_.Finish([this, client, reply]
    {
        if (client->processId)
        {
            AllowSetForegroundWindow(client->processId);
        }
        reply(Success());
        if (windowClient_ != client)
        {
            return;
        }
        if (!offered_.empty())
        {
            windowClient_->Send(Json{{"type", "activations"}});
        }
        ShowSettings();
        if (IsExiting())
        {
            windowClient_->Send(Json{{"type", "close"}});
            waitingSince_ = GetTickCount64();
        }
    });
}

void Application::OnClosed(Reply const& reply)
{
    reply(Success());
    ForgetWindow();
    if (!IsExiting())
    {
        Notify({.kind = NoticeKind::Background});
    }
}

// The window answers `activate`: whether it could show itself.
void Application::OnActivateReply(std::shared_ptr<Pipe::Connection> const& client, bool available, Reply const& reply)
{
    if (windowClient_ != client)
    {
        reply(Failure(ErrorCode::InvalidRequest));
        return;
    }
    if (IsExiting() || engine_->IsShuttingDown())
    {
        reply(Success());
        return;
    }
    reopen_ = !available;
    if (!reopen_)
    {
        waitingSince_ = 0;
        splash_.Close();
    }
    reply(Success());
}

void Application::OnCloseReply(std::shared_ptr<Pipe::Connection> const& client, CloseState state, Reply const& reply)
{
    if (windowClient_ != client || (!IsExiting() &&
        !(exit_ == ExitPhase::WindowConfirming && state == CloseState::Waiting)))
    {
        reply(Failure(ErrorCode::InvalidRequest));
        return;
    }
    reply(Success());
    if (state == CloseState::Waiting)
    {
        waitingSince_ = 0;
        splash_.Close();
        return;
    }
    if (state == CloseState::Closing)
    {
        waitingSince_ = GetTickCount64();
        return;
    }
    splash_.Close();
    CancelExit();
}

void Application::OnExitReply(std::shared_ptr<Pipe::Connection> const& client, ExitAnswer answer, Reply const& reply)
{
    if (windowClient_ != client || exit_ != ExitPhase::WindowConfirming)
    {
        reply(Failure(ErrorCode::InvalidRequest));
        return;
    }
    reply(Success());
    exit_ = ExitPhase::Idle;
    waitingSince_ = 0;
    if (engine_->IsShuttingDown())
    {
        return;
    }
    if (answer == ExitAnswer::Confirmed || (answer == ExitAnswer::Unavailable && ConfirmExit()))
    {
        BeginExit();
    }
}

void Application::OnPendingActivations(std::shared_ptr<Pipe::Connection> const& client, Reply const& reply)
{
    if (windowClient_ != client)
    {
        reply(Failure(ErrorCode::InvalidRequest));
        return;
    }
    auto activations = Json::array();
    for (auto const& activation : offered_)
    {
        activations.push_back({{"activation_id", activation.activationId}, {"sources", activation.sources}});
    }
    reply(Success({{"activations", std::move(activations)}}));
}

// The window has taken these activations, so they stop waiting.
void Application::OnActivationsReceived(std::shared_ptr<Pipe::Connection> const& client, std::vector<std::string> const& ids,
    Reply const& reply)
{
    if (windowClient_ != client || ids.size() > sourceLimit)
    {
        reply(Failure(ErrorCode::InvalidRequest));
        return;
    }
    std::erase_if(offered_, [&ids](Activation const& activation)
    {
        return std::find(ids.begin(), ids.end(), activation.activationId) != ids.end();
    });
    reply(Success());
}

void Application::Disconnect(std::shared_ptr<Pipe::Connection> const& client)
{
    engine_->Disconnect(client->connectionId);
    if (windowClient_ == client)
    {
        if (IsExiting() && IsWindowRunning())
        {
            windowClient_.reset();
            waitingSince_ = GetTickCount64();
            return;
        }
        ForgetWindow();
    }
}

void Application::ForgetWindow()
{
    windowClient_.reset();
    if (exit_ == ExitPhase::WindowConfirming)
    {
        exit_ = ExitPhase::Idle;
    }
    waitingSince_ = 0;
    splash_.Close();
    if (IsExiting())
    {
        Shutdown();
    }
}

bool Application::Open()
{
    if (IsExiting() || engine_->IsShuttingDown())
    {
        return false;
    }
    reopen_ = true;
    if (windowClient_)
    {
        if (windowClient_->processId)
        {
            AllowSetForegroundWindow(windowClient_->processId);
        }
        windowClient_->Send(Json{{"type", "activate"}});
        waitingSince_ = GetTickCount64();
        return true;
    }
    if (IsWindowRunning())
    {
        if (!waitingSince_)
        {
            waitingSince_ = GetTickCount64();
            launch_ = engine_->IsLoading() ? Launch::Cold : Launch::Warm;
            ShowSplash();
        }
        return true;
    }
    process_.reset();
    reopen_ = false;
    // Starting the window process takes time, so the splash appears first.
    waitingSince_ = GetTickCount64();
    launch_ = engine_->IsLoading() ? Launch::Cold : Launch::Warm;
    ShowSplash();
    auto executable = WindowExecutable();
    std::wstring arguments = L"\"" + executable.wstring() + L"\"";
    STARTUPINFOW startup{sizeof(startup)};
    PROCESS_INFORMATION process{};
    // Only a warm launch times the window alone; a cold one also waits for the
    // engine to load.
    launchedAt_ = launch_ == Launch::Warm ? GetTickCount64() : 0;
    if (!CreateProcessW(executable.c_str(), arguments.data(), nullptr, nullptr, FALSE, 0, nullptr,
        executable.parent_path().c_str(), &startup, &process))
    {
        auto error = GetLastError();
        waitingSince_ = 0;
        showsSettings_ = false;
        splash_.Close();
        ShowError("launch", executable.wstring() + L"\n" + std::to_wstring(error));
        return true;
    }
    CloseHandle(process.hThread);
    process_.reset(process.hProcess);
    AllowSetForegroundWindow(process.dwProcessId);
    return true;
}

bool Application::IsWindowRunning() const
{
    return process_ && WaitForSingleObject(process_.get(), 0) == WAIT_TIMEOUT;
}

void Application::Exit()
{
    if (saving_)
    {
        return;
    }
    if (engine_->IsShuttingDown())
    {
        Shutdown();
        return;
    }
    if (exit_ != ExitPhase::Idle)
    {
        return;
    }
    auto activity = engine_->Activity();
    if (headless_ || !activity.confirmsExit || activity.activeCount == 0)
    {
        BeginExit();
        return;
    }
    // The open window asks in its own style; the host asks only without one.
    if (windowClient_)
    {
        if (windowClient_->processId)
        {
            AllowSetForegroundWindow(windowClient_->processId);
        }
        exit_ = ExitPhase::WindowConfirming;
        waitingSince_ = GetTickCount64();
        windowClient_->Send(Json{{"type", "confirm_exit"}});
        return;
    }
    if (ConfirmExit())
    {
        BeginExit();
    }
}

bool Application::ConfirmExit()
{
    auto title = strings_.Text("exit", "title");
    auto detail = strings_.Text("exit", "active");
    auto label = strings_.Text("tray", "exit");
    TASKDIALOG_BUTTON button{IDOK, label.c_str()};
    TASKDIALOGCONFIG dialog{sizeof(dialog)};
    auto window = windowClient_ ? FindWinUiWindow(windowClient_->processId) : nullptr;
    dialog.hwndParent = window && IsWindowVisible(window) && !IsIconic(window) ? window : nullptr;
    dialog.dwCommonButtons = TDCBF_CANCEL_BUTTON;
    dialog.pszWindowTitle = productName;
    dialog.pszMainInstruction = title.c_str();
    dialog.pszContent = detail.c_str();
    dialog.cButtons = 1;
    dialog.pButtons = &button;
    dialog.nDefaultButton = IDOK;
    int chosen = IDCANCEL;
    exit_ = ExitPhase::Confirming;
    auto outcome = TaskDialogIndirect(&dialog, &chosen, nullptr, nullptr);
    // Windows can end the session while the dialog is open.
    if (exit_ != ExitPhase::Confirming)
    {
        return false;
    }
    exit_ = ExitPhase::Idle;
    return SUCCEEDED(outcome) && chosen == IDOK && !engine_->IsShuttingDown();
}

void Application::BeginExit()
{
    splash_.Close();
    exit_ = ExitPhase::Exiting;
    Refresh();
    if (windowClient_)
    {
        windowClient_->Send(Json{{"type", "close"}});
        waitingSince_ = GetTickCount64();
    }
    else if (IsWindowRunning())
    {
        waitingSince_ = GetTickCount64();
    }
    else
    {
        Shutdown();
    }
}

void Application::Shutdown()
{
    if (saving_)
    {
        return;
    }
    saving_ = true;
    waitingSince_ = 0;
    engine_->Shutdown([this](std::optional<std::string> failure)
    {
        saving_ = false;
        if (exit_ == ExitPhase::SessionEnding)
        {
            endSaved_ = !failure;
            return;
        }
        if (!failure)
        {
            PostQuitMessage(0);
        }
        else if (headless_)
        {
            PostQuitMessage(1);
        }
        else
        {
            ShowError("save", Wide(*failure));
        }
    });
}

// Windows is signing out or shutting down. Saves within the time Windows
// allows, and ends the process when the save does not finish in time.
void Application::EndSession()
{
    exit_ = ExitPhase::SessionEnding;
    splash_.Close();
    Refresh();
    Shutdown();
    auto deadline = GetTickCount64() + sessionEndTimeout;
    while (!endSaved_.has_value())
    {
        auto now = GetTickCount64();
        if (now >= deadline)
        {
            break;
        }
        auto remaining = static_cast<DWORD>(deadline - now);
        MsgWaitForMultipleObjectsEx(0, nullptr, remaining, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
        MSG message{};
        while (GetTickCount64() < deadline && PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
        {
            if (message.message == WM_QUIT)
            {
                continue;
            }
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
    }
    if (!endSaved_.has_value())
    {
        TerminateProcess(GetCurrentProcess(), 1);
    }
    PostQuitMessage(*endSaved_ ? 0 : 1);
}

void Application::Tick()
{
    if (ticking_)
    {
        return;
    }
    ticking_ = true;
    engine_->Tick();
    Refresh();
    AddSources();
    if (pendingStart_ && engine_->HasSettings())
    {
        pendingStart_ = false;
        if (!engine_->StartsInTray())
        {
            Open();
        }
    }
    if (reopen_ && !waitingSince_ && !windowClient_ && !IsWindowRunning())
    {
        reopen_ = false;
        Open();
    }
    if (!programsChecked_ && !headless_ && !engine_->IsLoading())
    {
        programsChecked_ = true;
        CheckPrograms();
    }
    for (auto& notice : engine_->TakeNotices())
    {
        Notify(std::move(notice));
    }
    tray_->Notify();
    ticking_ = false;
}

bool Application::IsExiting() const
{
    return exit_ == ExitPhase::Exiting || exit_ == ExitPhase::SessionEnding;
}

// Windows ending the session cannot be cancelled.
void Application::CancelExit()
{
    if (exit_ == ExitPhase::Exiting)
    {
        exit_ = ExitPhase::Idle;
    }
    waitingSince_ = 0;
    Refresh();
}

// Brings the tray, the power request, the splash theme and the text language
// in line with the engine and with Exit.
void Application::Refresh()
{
    activity_ = engine_->Activity();
    bool loading = engine_->IsLoading();
    tray_->Update(activity_, loading, !loading && !engine_->HasStorageFailure() && !IsExiting());
    power_.Update(activity_, IsExiting() || engine_->IsShuttingDown());
    splash_.SetTheme(engine_->Theme());
    ShowSplash();
    auto language = engine_->Language();
    if (language == strings_.Language())
    {
        return;
    }
    strings_ = Strings(language);
    tray_->Translate();
    splash_.Translate();
    power_.Translate();
}

// The splash covers the wait for a window to appear when the saved choice
// allows it. Before startup has read the settings that choice is unknown, and
// Refresh asks again once they are read.
void Application::ShowSplash()
{
    if (!waitingSince_ || windowClient_ || IsExiting() || headless_ || !engine_->HasSettings() ||
        !engine_->ShowsSplash())
    {
        return;
    }
    splash_.Show(launch_);
}

void Application::Pause()
{
    engine_->PauseSession(!activity_.pausedByChoice, [this](Outcome outcome)
    {
        if (outcome.error)
        {
            auto detail = Utf8(strings_.Text("error", "pause")) + " " + outcome.detail;
            Notify({.kind = NoticeKind::Failure, .name = Utf8(productName), .detail = detail});
        }
        Refresh();
    });
}

bool Application::ValidSources(std::vector<std::string> const& sources)
{
    return !sources.empty() && sources.size() <= sourceLimit &&
        std::all_of(sources.begin(), sources.end(), Engine::IsSource);
}

// Accepts sources from a second launch or from the shell. They wait until
// AddSources hands them to the window or adds them directly.
Json Application::Activate(std::vector<std::string> sources)
{
    std::size_t count = 0;
    for (auto const& activation : incoming_)
    {
        count += activation.sources.size();
    }
    for (auto const& activation : offered_)
    {
        count += activation.sources.size();
    }
    if (!ValidSources(sources) || count + sources.size() > sourceLimit)
    {
        return Failure(ErrorCode::InvalidSources);
    }
    if (IsExiting() || engine_->IsShuttingDown() || engine_->HasStorageFailure())
    {
        return Failure(ErrorCode::Unavailable);
    }
    auto id = std::to_string(++sequence_);
    incoming_.push_back({id, std::move(sources)});
    PostMessageW(owner_.get(), wake, 0, 0);
    return Success({{"activation_id", id}});
}

// Hands waiting sources to the window when it shows the Add dialog; otherwise
// adds the first waiting source directly, one at a time.
void Application::AddSources()
{
    if (adding_ || incoming_.empty() || engine_->IsLoading() || IsExiting() || engine_->IsShuttingDown())
    {
        return;
    }
    if (engine_->HasStorageFailure())
    {
        auto detail = engine_->StartupError();
        for (auto const& activation : incoming_)
        {
            for (auto const& source : activation.sources)
            {
                Notify({.kind = NoticeKind::AddFailed, .name = source, .detail = detail});
            }
        }
        incoming_.clear();
        return;
    }
    if (engine_->ShowsAdd())
    {
        while (!incoming_.empty())
        {
            offered_.push_back(std::move(incoming_.front()));
            incoming_.pop_front();
        }
        if (windowClient_)
        {
            windowClient_->Send(Json{{"type", "activations"}});
        }
        if (!windowClient_ || engine_->RaisesAdd())
            Open();
        return;
    }
    adding_ = true;
    engine_->Add(incoming_.front().sources.front(), [this](Outcome outcome, Added added)
    {
        FinishSource(outcome, added);
    });
}

void Application::FinishSource(Outcome const& outcome, Added const& added)
{
    auto& activation = incoming_.front();
    auto const& source = activation.sources.front();
    if (outcome.error)
    {
        if (headless_)
        {
            ShowError("source", Wide(source) + L"\n" + Wide(outcome.detail));
        }
        else
        {
            Notify({.kind = NoticeKind::AddFailed, .name = source, .detail = outcome.detail});
        }
    }
    else
    {
        auto kind = added.kind == AdditionKind::Duplicate ? NoticeKind::Duplicate : NoticeKind::Added;
        Notify({.kind = kind, .name = engine_->Name(added.torrentId), .torrentId = added.torrentId});
    }
    activation.sources.erase(activation.sources.begin());
    if (activation.sources.empty())
    {
        incoming_.pop_front();
    }
    adding_ = false;
    PostMessageW(owner_.get(), wake, 0, 0);
}

// Reports torrent handlers that start a missing program once each, so a
// problem the person leaves alone does not return at every start. A program
// stays reported after it is no longer found, because a check that times out
// would otherwise report it again at the next start. It runs once loading has
// ended, so it never delays opening TinyTorrent, and records nothing while
// problem notifications are off, so turning them on reports what is still
// broken.
void Application::CheckPrograms()
{
    if (!activity_.notifiesProblems)
    {
        return;
    }
    auto reported = activity_.reportedPrograms;
    std::vector<std::string> fresh;
    for (auto& program : registration_.MissingPrograms())
    {
        if (std::ranges::find(reported, program) == reported.end())
        {
            fresh.push_back(program);
        }
    }
    if (fresh.empty())
    {
        return;
    }
    reported.insert(reported.end(), fresh.begin(), fresh.end());
    Notice notice{.kind = NoticeKind::MissingProgram, .name = fresh.front(),
        .count = static_cast<unsigned>(fresh.size())};
    engine_->RecordPrograms(std::move(reported), [this, notice](Outcome outcome)
    {
        if (!outcome.error)
        {
            Notify(notice);
        }
    });
}

// Opens Settings in the window, where the person repairs handlers that start
// a missing program; a window still opening receives it once it is ready.
void Application::ShowSettings()
{
    if (showsSettings_ && windowClient_)
    {
        showsSettings_ = false;
        windowClient_->Send(Json{{"type", "settings"}});
    }
}

void Application::Notify(Notice notice)
{
    if (windowClient_ && UsesWindow(notice.kind))
    {
        windowClient_->Send(Json{{"type", "notice"}, {"kind", ToString(notice.kind)},
            {"torrent_id", notice.torrentId}, {"name", notice.name}, {"detail", notice.detail}, {"code", notice.code},
            {"count", notice.count}});
        return;
    }
    tray_->Queue(std::move(notice));
}

void Application::ShowError(std::string const& key, std::wstring detail)
{
    auto message = strings_.Text("error", key);
    if (!detail.empty())
    {
        message += L"\n\n" + detail;
    }
    if (headless_)
    {
        OutputDebugStringW(message.c_str());
        return;
    }
    // The window may be the one that stopped responding.
    tray_->Queue({.kind = NoticeKind::Failure, .name = Utf8(productName), .detail = Utf8(message)});
}
}
