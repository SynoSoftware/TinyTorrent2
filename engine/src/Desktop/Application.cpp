#include "Desktop/Application.h"
#include "Resources.h"
#include <algorithm>
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
constexpr UINT_PTR tickTimer = 1;

constexpr std::pair<std::string_view, Command> commands[] = {
    {"registration", Command::Registration},
    {"ready", Command::Ready},
    {"ui_closed", Command::UiClosed},
    {"activate_reply", Command::ActivateReply},
    {"close_reply", Command::CloseReply},
    {"activate_sources", Command::ActivateSources},
    {"pending_sources", Command::PendingSources},
    {"sources_received", Command::SourcesReceived},
    {"open", Command::Open},
    {"exit", Command::Exit}};

constexpr std::pair<std::string_view, CloseState> closeStates[] = {
    {"waiting", CloseState::Waiting},
    {"cancelled", CloseState::Cancelled},
    {"closing", CloseState::Closing}};

// Durations in milliseconds, as GetTickCount64 counts.
constexpr UINT tickInterval = 1000;
// How long the window may take to open or to close before the splash window
// reports it.
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

// TinyTorrent.exe beside the engine. A development build finds it in the
// WinUI project's output under the same bin folder.
// The window from the same build, which is always beside the engine.
std::filesystem::path WindowExecutable()
{
    return std::filesystem::path(Executable()).parent_path() / L"TinyTorrent.exe";
}

HWND FindWinUiWindow(DWORD process)
{
    struct Target
    {
        DWORD process;
        HWND window = nullptr;
    } target{process};
    EnumWindows([](HWND window, LPARAM parameter) -> BOOL
    {
        auto& target = *reinterpret_cast<Target*>(parameter);
        DWORD owner = 0;
        GetWindowThreadProcessId(window, &owner);
        if (owner != target.process)
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
// when the person pins the window of `process`.
void SetRelaunch(DWORD process)
{
    auto window = FindWinUiWindow(process);
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
      splash_(strings_, [this] { return IsWindowRunning(); },
          [this](SplashFailure failure, SplashChoice choice) { Resolve(failure, choice); }),
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
    // Procedure stores the handle in window_ while the window is created,
    // because its first messages already need it.
    CreateWindowW(type.lpszClassName, productName, 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, module, this);
    if (!window_)
    {
        throw std::runtime_error("Cannot create the engine owner window.");
    }
    type.lpfnWndProc = Broadcast;
    // Message-only windows do not receive Explorer's restart broadcast.
    type.lpszClassName = L"TinyTorrent.Broadcast";
    RegisterClassW(&type);
    broadcast_.reset(CreateWindowExW(WS_EX_TOOLWINDOW, type.lpszClassName, productName, 0, 0, 0, 0, 0,
        nullptr, nullptr, module, window_.get()));
    if (!broadcast_)
    {
        throw std::runtime_error("Cannot create the broadcast window.");
    }
    tray_ = std::make_unique<Tray>(window_.get(), tray, broadcast_.get(), strings_, headless_);
    auto data = directory.wstring();
    engine_ = std::make_unique<Engine>(std::move(directory),
        [this] { PostMessageW(window_.get(), wake, 0, 0); });
    Json hello{
        {"type", "hello"},
        {"version", Pipe::version},
        {"session_id", engine_->SessionId()},
        {"engine_path", Utf8(Executable())},
        {"data_directory", Utf8(data)}};
    {
        Security security(sid);
        pipe_ = std::make_unique<Pipe>(sid, security.Attributes(), std::move(hello),
            [this](Pipe::Client client, Json request, Reply reply)
            {
                Dispatch(std::move(client), std::move(request), std::move(reply));
            });
    }
    SetTimer(window_.get(), tickTimer, tickInterval, nullptr);
    powerNotification_.reset(RegisterPowerSettingNotification(window_.get(), &GUID_ACDC_POWER_SOURCE,
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
    else if (!background && !headless_)
    {
        Open();
    }
    MSG message{};
    while (GetMessageW(&message, nullptr, 0, 0) > 0)
    {
        if (splash_.PreTranslate(message))
        {
            continue;
        }
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
    return static_cast<int>(message.wParam);
}

LRESULT CALLBACK Application::Procedure(HWND window, UINT message, WPARAM first, LPARAM second)
{
    auto owner = reinterpret_cast<Application*>(GetWindowLongPtrW(window, GWLP_USERDATA));
    if (message == WM_NCCREATE)
    {
        owner = static_cast<Application*>(reinterpret_cast<CREATESTRUCTW*>(second)->lpCreateParams);
        owner->window_.reset(window);
        SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(owner));
    }
    if (!owner)
    {
        return DefWindowProcW(window, message, first, second);
    }
    return owner->Handle(message, first, second);
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
    case WM_TIMER:
        OnTimer();
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
        tray_->Add();
        Refresh();
        return 0;
    }
    return DefWindowProcW(window_.get(), message, first, second);
}

void Application::OnTray(WPARAM first, LPARAM second)
{
    auto notification = LOWORD(second);
    if (notification == NIN_SELECT || notification == NIN_KEYSELECT || notification == WM_LBUTTONDBLCLK)
    {
        Open();
    }
    else if (notification == WM_RBUTTONUP || notification == WM_CONTEXTMENU)
    {
        Refresh();
        POINT point{static_cast<short>(LOWORD(first)), static_cast<short>(HIWORD(first))};
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
    else if (notification == NIN_BALLOONUSERCLICK)
    {
        auto shown = tray_->TakeNotification();
        if (!shown)
        {
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
        tray_->TakeNotification();
    }
}

void Application::OnTimer()
{
    Tick();
    if (waitingSince_ && process_ && WaitForSingleObject(process_.get(), 0) == WAIT_OBJECT_0 && !ui_)
    {
        if (exiting_)
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
                ShowSplash(SplashFailure::Startup, std::to_wstring(code));
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
        if (!exiting_)
        {
            ShowSplash(SplashFailure::Startup);
        }
        else if (headless_)
        {
            Shutdown();
        }
        else
        {
            ShowSplash(SplashFailure::Unresponsive);
        }
    }
    splash_.Update();
}

void Application::Dispatch(Pipe::Client client, Json request, Reply reply)
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
    PostMessageW(window_.get(), dispatch, 0, 0);
}

// Commands about the window and launch sources are answered here; the engine
// answers all others.
void Application::Receive(Pipe::Client const& client, Json const& request, Reply const& reply)
{
    if (request.is_null())
    {
        Disconnect(client);
        return;
    }
    auto command = Parse(commands, request.at("command").get<std::string>());
    if (ending_ || (command == Command::Registration && engine_->IsStopping()))
    {
        reply(Failure(ErrorCode::Stopping));
        return;
    }
    // Only the window reports that it closed; the engine answers the same
    // command from any other client as unknown.
    if (command == Command::UiClosed && ui_ != client)
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
            reply(registration_.Execute(request.at("operation").get<std::string>()));
            break;
        case Command::Ready:
            OnReady(client, reply);
            break;
        case Command::UiClosed:
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
        case Command::ActivateSources:
            reply(Activate(request.at("sources").get<std::vector<std::string>>()));
            break;
        case Command::PendingSources:
            OnPendingSources(client, reply);
            break;
        case Command::SourcesReceived:
            OnSourcesReceived(client,
                request.value("activation_ids", std::vector<std::string>{}), reply);
            break;
        case Command::Open:
            if (!Open())
            {
                reply(Failure(ErrorCode::Stopping));
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

// The window has started and takes over from the splash window.
void Application::OnReady(Pipe::Client const& client, Reply const& reply)
{
    if (ui_ && ui_ != client)
    {
        reply(Failure(ErrorCode::UiConnected));
        return;
    }
    ui_ = client;
    if (auto process = client->process)
    {
        auto actual = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, process);
        if (actual)
        {
            process_.reset(actual);
        }
        SetRelaunch(process);
    }
    waitingSince_ = 0;
    reopen_ = false;
    splash_.Close();
    reply(Success());
    if (!offered_.empty())
    {
        ui_->Send(Json{{"type", "sources"}});
    }
    if (exiting_)
    {
        ui_->Send(Json{{"type", "close"}});
        waitingSince_ = GetTickCount64();
    }
}

void Application::OnClosed(Reply const& reply)
{
    reply(Success());
    ForgetWindow();
    if (!exiting_)
    {
        ExplainBackground();
    }
}

// The window answers `activate`: whether it could show itself.
void Application::OnActivateReply(Pipe::Client const& client, bool available, Reply const& reply)
{
    if (ui_ != client)
    {
        reply(Failure(ErrorCode::InvalidRequest));
        return;
    }
    if (exiting_ || engine_->IsStopping())
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

void Application::OnCloseReply(Pipe::Client const& client, CloseState state, Reply const& reply)
{
    if (ui_ != client || !exiting_)
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

void Application::OnPendingSources(Pipe::Client const& client, Reply const& reply)
{
    if (ui_ != client)
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
void Application::OnSourcesReceived(Pipe::Client const& client, std::vector<std::string> const& ids,
    Reply const& reply)
{
    if (ui_ != client || ids.size() > sourceLimit)
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

void Application::Disconnect(Pipe::Client const& client)
{
    engine_->Disconnect(client->connectionId);
    if (ui_ == client)
    {
        if (exiting_ && IsWindowRunning())
        {
            ui_.reset();
            waitingSince_ = GetTickCount64();
            return;
        }
        ForgetWindow();
    }
}

void Application::ForgetWindow()
{
    ui_.reset();
    waitingSince_ = 0;
    if (exiting_)
    {
        Shutdown();
    }
}

// The first time the window closes while transfers continue, a notice says
// where TinyTorrent went.
void Application::ExplainBackground()
{
    if (headless_ || noticeSaving_ || engine_->Activity().backgroundNoticeShown)
    {
        return;
    }
    noticeSaving_ = true;
    engine_->RecordBackgroundNotice([this](Outcome outcome)
    {
        noticeSaving_ = false;
        if (!outcome.error)
        {
            Notify({.kind = NoticeKind::Background});
        }
    });
}

bool Application::Open()
{
    if (exiting_ || engine_->IsStopping())
    {
        return false;
    }
    reopen_ = true;
    if (ui_)
    {
        if (ui_->process)
        {
            AllowSetForegroundWindow(ui_->process);
        }
        ui_->Send(Json{{"type", "activate"}});
        waitingSince_ = GetTickCount64();
        return true;
    }
    if (IsWindowRunning())
    {
        if (!waitingSince_)
        {
            waitingSince_ = GetTickCount64();
            ShowSplash();
        }
        return true;
    }
    process_.reset();
    reopen_ = false;
    auto executable = WindowExecutable();
    std::wstring arguments = L"\"" + executable.wstring() + L"\"";
    STARTUPINFOW startup{sizeof(startup)};
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(executable.c_str(), arguments.data(), nullptr, nullptr, FALSE, 0, nullptr,
        executable.parent_path().c_str(), &startup, &process))
    {
        waitingSince_ = 0;
        ShowSplash(SplashFailure::Launch, executable.wstring() + L"\n" + std::to_wstring(GetLastError()));
        return true;
    }
    CloseHandle(process.hThread);
    process_.reset(process.hProcess);
    AllowSetForegroundWindow(process.dwProcessId);
    waitingSince_ = GetTickCount64();
    ShowSplash();
    return true;
}

bool Application::IsWindowRunning() const
{
    return process_ && WaitForSingleObject(process_.get(), 0) == WAIT_TIMEOUT;
}

void Application::Exit()
{
    if (exiting_ || engine_->IsStopping())
    {
        return;
    }
    exiting_ = true;
    Refresh();
    if (ui_)
    {
        ui_->Send(Json{{"type", "close"}});
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
    if (!ending_ && !headless_ && engine_->Activity().filesBusy)
    {
        waitingSince_ = 0;
        ShowSplash(SplashFailure::FilesBusy);
        return;
    }
    BeginShutdown();
}

void Application::BeginShutdown()
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
        if (ending_)
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
            ShowSplash(SplashFailure::Save, Wide(*failure));
        }
    });
}

// Windows is signing out or shutting down. Saves within the time Windows
// allows, and ends the process when the save does not finish in time.
void Application::EndSession()
{
    ending_ = true;
    exiting_ = true;
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
    if (reopen_ && !waitingSince_ && !ui_ && !IsWindowRunning())
    {
        reopen_ = false;
        Open();
    }
    for (auto& notice : engine_->TakeNotices())
    {
        Notify(std::move(notice));
    }
    tray_->Notify();
    ticking_ = false;
}

void Application::CancelExit()
{
    exiting_ = false;
    waitingSince_ = 0;
    Refresh();
}

// Brings the tray, the power request and the text language in line with the
// engine and with Exit.
void Application::Refresh()
{
    activity_ = engine_->Activity();
    bool loading = engine_->IsLoading();
    tray_->Update(activity_, loading, !loading && !engine_->HasStorageFailure() && !exiting_);
    power_.Update(activity_, exiting_ || engine_->IsStopping());
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

void Application::Pause()
{
    engine_->PauseSession(!activity_.allPaused, [this](Outcome outcome)
    {
        if (outcome.error)
        {
            auto detail = Utf8(strings_.Text("error", "pause")) + " " + outcome.detail;
            // The window did not send this command, so it cannot show the
            // failure even when it is open.
            tray_->Queue({.kind = NoticeKind::Error, .name = Utf8(productName), .detail = detail}, false);
        }
        Refresh();
    });
}

void Application::ShowSplash(std::optional<SplashFailure> failure, std::wstring detail)
{
    if (!headless_)
    {
        splash_.Show(failure, std::move(detail));
    }
    else if (failure)
    {
        ShowError(ToString(*failure), detail);
    }
}

void Application::Resolve(SplashFailure failure, SplashChoice choice)
{
    switch (failure)
    {
    case SplashFailure::Launch:
    case SplashFailure::Startup:
        if (choice == SplashChoice::Retry)
        {
            Open();
        }
        else
        {
            reopen_ = false;
        }
        break;
    case SplashFailure::Save:
        if (choice == SplashChoice::Retry)
        {
            Shutdown();
        }
        else
        {
            PostQuitMessage(1);
        }
        break;
    case SplashFailure::FilesBusy:
        if (choice == SplashChoice::Wait)
        {
            BeginShutdown();
        }
        else
        {
            CancelExit();
            Open();
        }
        break;
    case SplashFailure::Unresponsive:
        if (choice == SplashChoice::ExitAnyway)
        {
            auto process = process_ ? GetProcessId(process_.get()) : 0;
            auto handle = process ? OpenProcess(PROCESS_TERMINATE, FALSE, process) : nullptr;
            if (handle)
            {
                TerminateProcess(handle, 1);
                CloseHandle(handle);
            }
            Shutdown();
        }
        else
        {
            CancelExit();
        }
        break;
    }
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
    if (exiting_ || engine_->IsStopping() || engine_->HasStorageFailure())
    {
        return Failure(ErrorCode::Unavailable);
    }
    auto id = std::to_string(++sequence_);
    incoming_.push_back({id, std::move(sources)});
    PostMessageW(window_.get(), wake, 0, 0);
    return Success({{"activation_id", id}});
}

// Hands waiting sources to the window when it shows the Add form; otherwise
// adds the first waiting source directly, one at a time.
void Application::AddSources()
{
    if (adding_ || incoming_.empty() || engine_->IsLoading() || exiting_ || engine_->IsStopping())
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
    if (ui_ || engine_->ShowsAdd())
    {
        while (!incoming_.empty())
        {
            offered_.push_back(std::move(incoming_.front()));
            incoming_.pop_front();
        }
        if (ui_)
        {
            ui_->Send(Json{{"type", "sources"}});
        }
        Open();
        return;
    }
    adding_ = true;
    engine_->Add(incoming_.front().sources.front(), [this](Outcome outcome, Added added)
    {
        if (!outcome.error && added.kind == AdditionKind::Mergeable)
        {
            offered_.push_back(std::move(incoming_.front()));
            incoming_.pop_front();
            adding_ = false;
            if (ui_)
            {
                ui_->Send(Json{{"type", "sources"}});
            }
            Open();
            PostMessageW(window_.get(), wake, 0, 0);
            return;
        }
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
    PostMessageW(window_.get(), wake, 0, 0);
}

void Application::Notify(Notice notice)
{
    tray_->Queue(std::move(notice), ui_ != nullptr);
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
    MessageBoxW(window_.get(), message.c_str(), productName, MB_OK | MB_ICONERROR);
}
}
