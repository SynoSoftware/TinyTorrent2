#include "Desktop/Window.h"
#include "Resources.h"
#include <algorithm>
#include <dwmapi.h>
#include <shellapi.h>
#include <shobjidl.h>
#include <propkey.h>
#include <propvarutil.h>

namespace tt::desktop
{
namespace
{
// How long the window may take to open or to close before reporting a
// failure, in milliseconds, as GetTickCount64 counts.
constexpr ULONGLONG windowTimeout = 30'000;

// WinUI 3 gives its top-level windows this class.
constexpr wchar_t winUiWindowClass[] = L"WinUIDesktopWin32WindowClass";

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

// The window stays cloaked until it is ready or has a connection failure to show.
bool IsShown(HWND window)
{
    DWORD cloaked = 0;
    return window && IsWindowVisible(window) &&
        SUCCEEDED(DwmGetWindowAttribute(window, DWMWA_CLOAKED, &cloaked, sizeof(cloaked))) && !cloaked;
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

bool ValidSources(std::vector<std::string> const& sources)
{
    return !sources.empty() && sources.size() <= sourceLimit &&
        std::all_of(sources.begin(), sources.end(), Engine::IsSource);
}

Window::Window(Strings const& strings, Engine& engine, Toasts& toasts, bool headless, Commands commands)
    : strings_(strings), engine_(engine), toasts_(toasts), headless_(headless), commands_(std::move(commands)),
      splash_(strings)
{
}

bool Window::Open()
{
    if (IsClosing() || engine_.IsShuttingDown())
    {
        return false;
    }
    reopen_ = true;
    if (client_)
    {
        if (client_->processId)
        {
            AllowSetForegroundWindow(client_->processId);
        }
        client_->Send(Json{{"type", "activate"}});
        // A window asking to confirm Exit keeps that wait, so a window that
        // stops answering still cancels Exit.
        if (wait_)
        {
            wait_->since = GetTickCount64();
        }
        else
        {
            wait_ = Wait{WindowWait::Activation, GetTickCount64()};
        }
        return true;
    }
    if (IsRunning())
    {
        // It will not send `ready`, so waiting would only end in a false report.
        if (auto window = Shown())
        {
            reopen_ = false;
            if (IsIconic(window))
            {
                ShowWindow(window, SW_RESTORE);
            }
            SetForegroundWindow(window);
            return true;
        }
        if (!wait_)
        {
            wait_ = Wait{WindowWait::Ready, GetTickCount64()};
            launch_ = engine_.IsLoading() ? Launch::Cold : Launch::Warm;
            ShowSplash();
        }
        return true;
    }
    process_.reset();
    reopen_ = false;
    // Starting the window process takes time, so the splash appears first.
    wait_ = Wait{WindowWait::Ready, GetTickCount64()};
    launch_ = engine_.IsLoading() ? Launch::Cold : Launch::Warm;
    ShowSplash();
    auto executable = WindowExecutable();
    std::wstring arguments = L"\"" + executable.wstring() + L"\" --engine";
    STARTUPINFOW startup{sizeof(startup)};
    PROCESS_INFORMATION process{};
    // Only a warm launch times the window alone; a cold one also waits for the
    // engine to load.
    launchedAt_ = launch_ == Launch::Warm ? std::optional(GetTickCount64()) : std::nullopt;
    if (!CreateProcessW(executable.c_str(), arguments.data(), nullptr, nullptr, FALSE, 0, nullptr,
        executable.parent_path().c_str(), &startup, &process))
    {
        auto error = GetLastError();
        wait_.reset();
        settingsCard_.reset();
        splash_.Close();
        auto key = "launch";
        switch (error)
        {
        case ERROR_FILE_NOT_FOUND:
        case ERROR_PATH_NOT_FOUND:
            key = "window_missing";
            break;
        case ERROR_ACCESS_DENIED:
            key = "window_denied";
            break;
        case ERROR_BAD_EXE_FORMAT:
        case ERROR_EXE_MACHINE_TYPE_MISMATCH:
            key = "window_invalid";
            break;
        case ERROR_MOD_NOT_FOUND:
        case ERROR_DLL_NOT_FOUND:
            key = "window_dependency";
            break;
        }
        wchar_t reason[2048]{};
        auto name = executable.filename().wstring();
        DWORD_PTR inserts[]{reinterpret_cast<DWORD_PTR>(name.c_str())};
        auto format = FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_MAX_WIDTH_MASK;
        format |= error == ERROR_BAD_EXE_FORMAT || error == ERROR_EXE_MACHINE_TYPE_MISMATCH ?
            FORMAT_MESSAGE_ARGUMENT_ARRAY : FORMAT_MESSAGE_IGNORE_INSERTS;
        FormatMessageW(format, nullptr, error, 0, reason, static_cast<DWORD>(std::size(reason)),
            reinterpret_cast<va_list*>(inserts));
        auto detail = strings_.Format("notification", "windows_error", {std::to_wstring(error)});
        if (*reason)
        {
            detail += L" \u00B7 " + std::wstring(reason);
        }
        ShowStartError({.code = key, .detail = executable.wstring() + L"\n" + detail, .interactive = true});
        return true;
    }
    CloseHandle(process.hThread);
    process_.reset(process.hProcess);
    AllowSetForegroundWindow(process.dwProcessId);
    return true;
}

void Window::OpenSettings(std::string card)
{
    // Set first, so a launch failure inside Open can clear it.
    settingsCard_ = std::move(card);
    if (!Open())
    {
        settingsCard_.reset();
    }
    ShowSettings();
}

void Window::Reopen()
{
    if (reopen_ && !wait_ && !client_ && !IsRunning())
    {
        reopen_ = false;
        Open();
    }
}

// The open window asks in its own style.
bool Window::Confirm()
{
    if (!client_)
    {
        return false;
    }
    if (client_->processId)
    {
        AllowSetForegroundWindow(client_->processId);
    }
    wait_ = Wait{WindowWait::Confirmation, GetTickCount64()};
    client_->Send(Json{{"type", "confirm_exit"}});
    return true;
}

bool Window::Close()
{
    // Closing the splash delivers a `ready` reply that it holds, whose
    // continuation sends `close` only once the wait below has begun, so a
    // window that is still starting gets one `close` as soon as it connects.
    splash_.Close();
    if (!client_ && !IsRunning())
    {
        wait_.reset();
        return false;
    }
    wait_ = Wait{WindowWait::Closing, GetTickCount64()};
    if (client_)
    {
        client_->Send(Json{{"type", "close"}});
    }
    return true;
}

void Window::End()
{
    wait_ = Wait{WindowWait::Closing, std::nullopt};
    splash_.Close();
}

void Window::Offer(Activation activation)
{
    offered_.push_back(std::move(activation));
    if (client_)
    {
        client_->Send(Json{{"type", "activations"}});
    }
    if (!client_ || engine_.RaisesAdd())
    {
        Open();
    }
}

std::size_t Window::Offered() const
{
    std::size_t count = 0;
    for (auto const& activation : offered_)
    {
        count += activation.sources.size();
    }
    return count;
}

bool Window::Notify(Toast const& toast)
{
    if (!client_)
    {
        return false;
    }
    auto toasts = Switch(toast.kind);
    client_->Send(Json{{"type", "toast"}, {"kind", ToString(toast.kind)},
        {"torrent_id", toast.torrentId}, {"name", toast.name}, {"detail", toast.detail}, {"code", toast.code},
        {"folder", toast.folder}, {"count", toast.count},
        {"switch", toasts ? Json(ToString(*toasts)) : Json(nullptr)}});
    return true;
}

// The window has drawn its first state and waits to appear.
void Window::OnReady(std::shared_ptr<Pipe::Connection> const& client, Reply const& reply)
{
    if (client_)
    {
        reply(Failure(ErrorCode::WindowConnected));
        return;
    }
    client_ = client;
    if (auto processId = client->processId)
    {
        auto actual = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, processId);
        if (actual)
        {
            process_.reset(actual);
        }
        SetRelaunch(processId);
    }
    if (!IsClosing())
    {
        wait_.reset();
    }
    reopen_ = false;
    if (launchedAt_)
    {
        splash_.Record(GetTickCount64() - *launchedAt_);
        launchedAt_.reset();
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
        if (client_ != client)
        {
            return;
        }
        if (!offered_.empty())
        {
            client_->Send(Json{{"type", "activations"}});
        }
        ShowSettings();
        if (IsClosing())
        {
            client_->Send(Json{{"type", "close"}});
            wait_->since = GetTickCount64();
        }
    });
}

void Window::OnClosed(Reply const& reply)
{
    reply(Success());
    auto closing = IsClosing();
    Forget();
    if (!closing)
    {
        toasts_.Queue({.kind = ToastKind::Background});
    }
}

// The window answers `activate`: whether it could show itself.
void Window::OnActivateReply(std::shared_ptr<Pipe::Connection> const& client, bool available, Reply const& reply)
{
    if (client_ != client)
    {
        reply(Failure(ErrorCode::InvalidRequest));
        return;
    }
    if (IsClosing() || engine_.IsShuttingDown())
    {
        reply(Success());
        return;
    }
    reopen_ = !available;
    if (available)
    {
        // A window that answers also answers its Exit prompt, so that wait
        // stops timing.
        if (wait_ && wait_->kind == WindowWait::Activation)
        {
            wait_.reset();
        }
        else if (wait_)
        {
            wait_->since.reset();
        }
        splash_.Close();
    }
    reply(Success());
}

void Window::OnCloseReply(std::shared_ptr<Pipe::Connection> const& client, CloseState state, Reply const& reply)
{
    auto confirming = wait_ && wait_->kind == WindowWait::Confirmation;
    if (client_ != client || !(IsClosing() || (confirming && state == CloseState::Waiting)))
    {
        reply(Failure(ErrorCode::InvalidRequest));
        return;
    }
    reply(Success());
    if (state == CloseState::Waiting)
    {
        wait_->since.reset();
        splash_.Close();
        return;
    }
    if (state == CloseState::Closing)
    {
        wait_->since = GetTickCount64();
        return;
    }
    wait_.reset();
    splash_.Close();
    commands_.cancelExit();
}

void Window::OnExitReply(std::shared_ptr<Pipe::Connection> const& client, ExitAnswer answer, Reply const& reply)
{
    if (client_ != client || !wait_ || wait_->kind != WindowWait::Confirmation)
    {
        reply(Failure(ErrorCode::InvalidRequest));
        return;
    }
    reply(Success());
    wait_.reset();
    commands_.answer(answer);
}

void Window::OnPendingActivations(std::shared_ptr<Pipe::Connection> const& client, Reply const& reply)
{
    if (client_ != client)
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
void Window::OnActivationsReceived(std::shared_ptr<Pipe::Connection> const& client,
    std::vector<std::string> const& ids, Reply const& reply)
{
    if (client_ != client || ids.size() > sourceLimit)
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

void Window::Disconnect(std::shared_ptr<Pipe::Connection> const& client)
{
    if (client_ != client)
    {
        return;
    }
    // A window closing for Exit may still save its own state.
    if (IsClosing() && IsRunning())
    {
        client_.reset();
        wait_->since = GetTickCount64();
        return;
    }
    Forget();
}

void Window::OnTimer()
{
    if (!wait_ || !wait_->since)
    {
        return;
    }
    if (process_ && WaitForSingleObject(process_.get(), 0) == WAIT_OBJECT_0 && !client_)
    {
        if (IsClosing())
        {
            wait_.reset();
            commands_.shutdown();
            return;
        }
        DWORD code = 0;
        GetExitCodeProcess(process_.get(), &code);
        if (code != 0)
        {
            wait_.reset();
            reopen_ = false;
            settingsCard_.reset();
            splash_.Close();
            ShowStartError({.code = "startup", .detail = std::to_wstring(code)});
            return;
        }
        if (reopen_)
        {
            wait_.reset();
            return;
        }
    }
    // A launched window that appears without `ready` shows its own failure,
    // so the splash closes and the engine reports nothing.
    if (wait_->kind == WindowWait::Ready && Shown())
    {
        wait_.reset();
        reopen_ = false;
        launchedAt_.reset();
        splash_.Close();
        return;
    }
    if (GetTickCount64() - *wait_->since <= windowTimeout)
    {
        return;
    }
    auto kind = wait_->kind;
    reopen_ = false;
    settingsCard_.reset();
    // A headless Exit goes on without the window, which still counts as
    // closing.
    if (kind == WindowWait::Closing && headless_)
    {
        wait_->since.reset();
    }
    else
    {
        wait_.reset();
    }
    if (kind == WindowWait::Ready || kind == WindowWait::Activation)
    {
        splash_.Close();
        ShowStartError({.code = "startup"});
    }
    else if (headless_)
    {
        commands_.shutdown();
    }
    else
    {
        commands_.cancelExit();
        // The window may be the one that stopped responding.
        toasts_.Report({.code = "unresponsive"});
    }
}

void Window::Refresh(Theme theme)
{
    splash_.SetTheme(theme);
    ShowSplash();
}

void Window::Translate()
{
    splash_.Translate();
}

bool Window::IsClosing() const
{
    return wait_ && wait_->kind == WindowWait::Closing;
}

bool Window::IsPresent() const
{
    return client_ || IsRunning();
}

bool Window::IsRunning() const
{
    return process_ && WaitForSingleObject(process_.get(), 0) == WAIT_TIMEOUT;
}

HWND Window::Shown() const
{
    if (client_ || !IsRunning())
    {
        return nullptr;
    }
    auto window = FindWinUiWindow(GetProcessId(process_.get()));
    return IsShown(window) ? window : nullptr;
}

// The window has gone: Exit that waited for it continues, and its Exit prompt
// counts as cancelled.
void Window::Forget()
{
    client_.reset();
    auto kind = wait_ ? std::optional(wait_->kind) : std::nullopt;
    wait_.reset();
    splash_.Close();
    if (kind == WindowWait::Confirmation)
    {
        commands_.cancelExit();
    }
    else if (kind == WindowWait::Closing)
    {
        commands_.shutdown();
    }
}

// The splash covers the wait for a window to appear when the saved choice
// allows it. Before startup has read the settings that choice is unknown, and
// Refresh asks again once they are read.
void Window::ShowSplash()
{
    if (!wait_ || wait_->kind != WindowWait::Ready || client_ || headless_ || !engine_.HasSettings() ||
        !engine_.ShowsSplash())
    {
        return;
    }
    splash_.Show(launch_);
}

void Window::ShowSettings()
{
    if (settingsCard_ && client_)
    {
        client_->Send(Json{{"type", "settings"}, {"setting", *settingsCard_}});
        settingsCard_.reset();
    }
}

// The window did not start: Retry opens it again, and Open folder shows the
// folder that holds the window program.
void Window::ShowStartError(Toasts::Failure failure)
{
    failure.actions.push_back(
        Toasts::Retry(strings_, strings_.Text("notification", "retry_tip"), [this] { Open(); }));
    failure.actions.push_back(Toasts::OpenFolder(strings_, WindowExecutable().parent_path(),
        strings_.Text("notification", "open_folder_tip")));
    toasts_.Report(std::move(failure));
}
}
