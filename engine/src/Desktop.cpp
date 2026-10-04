#include "Desktop.h"
#include <algorithm>
#include <sddl.h>
#include <shellapi.h>
#include <shobjidl.h>
#include <propkey.h>
#include <propvarutil.h>
#include <stdexcept>

namespace tiny
{
namespace
{
constexpr UINT dispatch = WM_APP + 1;
constexpr UINT tray = WM_APP + 2;
constexpr UINT wake = WM_APP + 3;

LRESULT CALLBACK Broadcast(HWND window, UINT message, WPARAM first, LPARAM second)
{
    if (message == WM_NCCREATE)
        SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(
            reinterpret_cast<CREATESTRUCTW*>(second)->lpCreateParams));
    auto owner = reinterpret_cast<HWND>(GetWindowLongPtrW(window, GWLP_USERDATA));
    if (owner && (message == WM_ENDSESSION || message == WM_POWERBROADCAST ||
        message == WM_MEASUREITEM || message == WM_DRAWITEM))
        return SendMessageW(owner, message, first, second);
    if (message == WM_QUERYENDSESSION) return TRUE;
    if (owner && message >= 0xc000)
        PostMessageW(owner, message, first, second);
    return DefWindowProcW(window, message, first, second);
}
}

Json LoadStrings(std::string const& language)
{
    auto module = GetModuleHandleW(nullptr);
    auto read = [module](WORD catalogue)
    {
        auto resource = FindResourceW(module, MAKEINTRESOURCEW(catalogue), RT_RCDATA);
        auto loaded = LoadResource(module, resource);
        return Json::parse(std::string_view(static_cast<char const*>(LockResource(loaded)),
            SizeofResource(module, resource)), nullptr, false);
    };
    auto strings = read(1);
    if (language == "es" || language.starts_with("es-"))
    {
        auto translated = read(3);
        if (translated.is_object()) strings.merge_patch(translated);
    }
    return strings;
}

std::wstring LogonSid()
{
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token))
        throw std::runtime_error("Cannot read the current logon token.");
    DWORD size = 0;
    GetTokenInformation(token, TokenGroups, nullptr, 0, &size);
    std::vector<char> storage(size);
    bool success = GetTokenInformation(token, TokenGroups, storage.data(), size, &size);
    CloseHandle(token);
    if (!success) throw std::runtime_error("Cannot read the current logon groups.");
    auto groups = reinterpret_cast<TOKEN_GROUPS*>(storage.data());
    for (DWORD index = 0; index < groups->GroupCount; ++index)
    {
        auto const& group = groups->Groups[index];
        if ((group.Attributes & SE_GROUP_LOGON_ID) != SE_GROUP_LOGON_ID) continue;
        LPWSTR text = nullptr;
        if (!ConvertSidToStringSidW(group.Sid, &text)) break;
        std::wstring sid(text);
        LocalFree(text);
        return sid;
    }
    throw std::runtime_error("No logon SID is available.");
}

Desktop::Desktop(std::filesystem::path directory, std::wstring sid, bool headless)
    : headless_(headless)
{
    auto module = GetModuleHandleW(nullptr);
    strings_ = LoadStrings();
    std::filesystem::create_directories(directory);
    directory = std::filesystem::canonical(directory);
    ownership_ = CreateFileW((directory / L"ownership.lock").c_str(), GENERIC_READ | GENERIC_WRITE,
        0, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (ownership_ == INVALID_HANDLE_VALUE)
        throw std::runtime_error(Utf8(Text("error", "ownership")) + " " + Utf8(directory.wstring()));
    try
    {
        WNDCLASSW type{};
        type.lpfnWndProc = Window;
        type.hInstance = module;
        type.hIcon = LoadIconW(module, MAKEINTRESOURCEW(2));
        type.lpszClassName = L"TinyTorrent.Owner";
        RegisterClassW(&type);
        window_ = CreateWindowW(type.lpszClassName, L"TinyTorrent", 0, 0, 0, 0, 0,
            HWND_MESSAGE, nullptr, module, this);
        if (!window_) throw std::runtime_error("Cannot create the engine owner window.");
        type.lpfnWndProc = Broadcast;
        // Message-only windows do not receive Explorer's restart broadcast.
        type.lpszClassName = L"TinyTorrent.Tray";
        RegisterClassW(&type);
        broadcast_ = CreateWindowExW(WS_EX_TOOLWINDOW, type.lpszClassName, L"TinyTorrent", 0, 0, 0, 0, 0,
            nullptr, nullptr, module, window_);
        if (!broadcast_) throw std::runtime_error("Cannot create the tray notification window.");
        type.lpfnWndProc = Surface;
        type.hbrBackground = GetSysColorBrush(COLOR_WINDOW);
        type.lpszClassName = L"TinyTorrent.Startup";
        RegisterClassW(&type);
        wchar_t executable[32768];
        GetModuleFileNameW(nullptr, executable, static_cast<DWORD>(std::size(executable)));
        auto dataDirectory = Utf8(directory.wstring());
        engine_ = std::make_unique<Engine>(std::move(directory),
            [this] { PostMessageW(window_, wake, 0, 0); });
        PSECURITY_DESCRIPTOR descriptor = nullptr;
        auto acl = L"D:P(A;;GA;;;" + sid + L")";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(acl.c_str(), SDDL_REVISION_1,
            &descriptor, nullptr)) throw std::runtime_error("Cannot create the pipe security descriptor.");
        SECURITY_ATTRIBUTES security{sizeof(security), descriptor, FALSE};
        try
        {
            pipe_ = std::make_unique<Pipe>(L"\\\\.\\pipe\\TinyTorrent." + sid, security,
                Json{{"type", "hello"}, {"version", 1}, {"session_id", engine_->Snapshot()["session_id"]},
                    {"engine_path", Utf8(executable)}, {"data_directory", dataDirectory}},
                [this](Pipe::Client client, Json request) { Dispatch(std::move(client), std::move(request)); });
        }
        catch (...) { LocalFree(descriptor); throw; }
        LocalFree(descriptor);
        explorer_ = RegisterWindowMessageW(L"TaskbarCreated");
        SetTimer(window_, 1, 1000, nullptr);
        power_notification_ = RegisterPowerSettingNotification(window_, &GUID_ACDC_POWER_SOURCE, DEVICE_NOTIFY_WINDOW_HANDLE);
        auto restart = L"--background --data \"" + Wide(dataDirectory) + L"\\.\"";
        RegisterApplicationRestart(restart.c_str(), RESTART_NO_PATCH | RESTART_NO_REBOOT);
    }
    catch (...)
    {
        pipe_.reset();
        engine_.reset();
        if (broadcast_) DestroyWindow(broadcast_);
        if (window_) DestroyWindow(window_);
        CloseHandle(ownership_);
        ownership_ = INVALID_HANDLE_VALUE;
        throw;
    }
}

Desktop::~Desktop()
{
    if (power_notification_) UnregisterPowerSettingNotification(power_notification_);
    if (awake_) PowerClearRequest(power_, PowerRequestSystemRequired);
    if (power_ != INVALID_HANDLE_VALUE) CloseHandle(power_);
    pipe_.reset();
    engine_.reset();
    if (process_) CloseHandle(process_);
    Tray(NIM_DELETE);
    if (splash_ && IsWindow(splash_)) DestroyWindow(splash_);
    if (font_) DeleteObject(font_);
    if (broadcast_) DestroyWindow(broadcast_);
    if (window_) DestroyWindow(window_);
    if (ownership_ != INVALID_HANDLE_VALUE) CloseHandle(ownership_);
}

std::wstring Desktop::Text(std::string const& group, std::string const& key) const
{
    auto messages = strings_.find(group);
    if (messages != strings_.end())
    {
        auto message = messages->find(key);
        if (message != messages->end() && message->is_string())
            return Wide(message->get<std::string>());
    }
    return Wide(group + "." + key);
}

void Desktop::Feedback(std::string const& key, std::wstring detail)
{
    auto message = Text("error", key);
    if (!detail.empty()) message += L"\n\n" + detail;
    if (headless_) { OutputDebugStringW(message.c_str()); return; }
    MessageBoxW(window_, message.c_str(), L"TinyTorrent", MB_OK | MB_ICONERROR);
}

void Desktop::Dispatch(Pipe::Client client, Json request)
{
    std::lock_guard lock(mutex_);
    if (request.is_null() && !client->dispatched) return;
    if (pending_.size() >= 128 && !request.is_null())
    {
        client->Send(Json{{"request_id", request["request_id"]}, {"ok", false},
            {"error", {{"code", "overloaded"}, {"detail", ""}}}});
        return;
    }
    client->dispatched = true;
    pending_.push_back([this, client = std::move(client), request = std::move(request)]
        { Receive(client, request); });
    PostMessageW(window_, dispatch, 0, 0);
}

void Desktop::Receive(Pipe::Client const& client, Json const& request)
{
    if (request.is_null())
    {
        engine_->Disconnect(client->connection_id);
        if (ui_ == client)
        {
            ui_.reset();
            opened_ = 0;
            if (exiting_) Shutdown();
        }
        return;
    }
    auto command = request.at("command").get<std::string>();
    if (ending_ || (command == "registration" && engine_->IsStopping()))
    {
        client->Send(Json{{"request_id", request["request_id"]}, {"ok", false},
            {"error", {{"code", "stopping"}, {"detail", ""}}}});
        return;
    }
    auto reply = [&] { client->Send(Json{{"request_id", request["request_id"]}, {"ok", true}, {"data", Json::object()}}); };
    if (command == "registration")
    {
        auto operation = request.find("operation");
        auto response = operation != request.end() && operation->is_string() ?
            registration_.Execute(operation->get<std::string>()) :
            Json{{"ok", false}, {"error", {{"code", "invalid_request"}, {"detail", ""}}}};
        response["request_id"] = request["request_id"];
        client->Send(std::move(response));
    }
    else if (command == "activate_reply")
    {
        if (ui_ != client || !request.contains("available") || !request["available"].is_boolean())
        {
            client->Send(Json{{"request_id", request["request_id"]}, {"ok", false},
                {"error", {{"code", "invalid_request"}, {"detail", ""}}}});
            return;
        }
        reopen_ = !request["available"].get<bool>();
        if (!reopen_)
        {
            opened_ = 0;
            if (splash_ && IsWindow(splash_)) DestroyWindow(splash_);
            splash_ = nullptr;
        }
        reply();
    }
    else if (command == "activate_sources")
    {
        auto response = Activate(request.value("sources", Json()));
        response["request_id"] = request["request_id"];
        client->Send(std::move(response));
    }
    else if (command == "pending_sources" || command == "sources_received")
    {
        auto ids = request.value("activation_ids", Json::array());
        if (ui_ != client || (command == "sources_received" &&
            (!ids.is_array() || ids.size() > 256 || std::any_of(ids.begin(), ids.end(),
                [](Json const& id) { return !id.is_string(); }))))
        {
            client->Send(Json{{"request_id", request["request_id"]}, {"ok", false},
                {"error", {{"code", "invalid_request"}, {"detail", ""}}}});
            return;
        }
        if (command == "sources_received")
        {
            std::erase_if(activations_, [&ids](Activation const& activation)
                { return std::find(ids.begin(), ids.end(), activation.activation_id) != ids.end(); });
            reply();
        }
        else
        {
            auto activations = Json::array();
            for (auto const& activation : activations_)
                activations.push_back({{"activation_id", activation.activation_id}, {"sources", activation.sources}});
            client->Send(Json{{"request_id", request["request_id"]}, {"ok", true},
                {"data", {{"activations", std::move(activations)}}}});
        }
    }
    else if (command == "open") { Open(); reply(); }
    else if (command == "ready")
    {
        if (ui_ && ui_ != client)
        {
            client->Send(Json{{"request_id", request["request_id"]}, {"ok", false},
                {"error", {{"code", "ui_connected"}, {"detail", ""}}}});
            return;
        }
        ui_ = client;
        ULONG process = 0;
        if (GetNamedPipeClientProcessId(client->handle, &process))
        {
            auto actual = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, process);
            if (actual) { if (process_) CloseHandle(process_); process_ = actual; }
            Relaunch(process);
        }
        opened_ = 0;
        reopen_ = false;
        if (splash_ && IsWindow(splash_)) DestroyWindow(splash_);
        splash_ = nullptr;
        reply();
        if (!activations_.empty()) ui_->Send(Json{{"type", "sources"}});
        if (exiting_)
        {
            ui_->Send(Json{{"type", "close"}});
            opened_ = GetTickCount64();
        }
    }
    else if (command == "ui_closed" && ui_ == client)
    {
        ui_.reset();
        opened_ = 0;
        reply();
        if (exiting_) Shutdown();
        else if (!headless_ && !notice_saving_ && !engine_->Activity().value("background_notice_shown", false))
        {
            notice_saving_ = true;
            engine_->Execute({{"command", "settings"}, {"changes", {{"background_notice_shown", true}}}},
                [this](Json response)
                {
                    notice_saving_ = false;
                    if (response.value("ok", false)) Notice({{"kind", "background"}});
                });
        }
    }
    else if (command == "exit") { reply(); Exit(); }
    else if (command == "close_reply")
    {
        if (ui_ != client || !request.contains("cancelled") || !request["cancelled"].is_boolean())
        {
            client->Send(Json{{"request_id", request["request_id"]}, {"ok", false},
                {"error", {{"code", "invalid_request"}, {"detail", ""}}}});
            return;
        }
        reply();
        if (request["cancelled"].get<bool>())
        {
            exiting_ = false;
            opened_ = 0;
            if (splash_ && IsWindow(splash_)) DestroyWindow(splash_);
            splash_ = nullptr;
            Power();
        }
    }
    else engine_->Execute(request, [this, client, request_id = request["request_id"]](Json response)
    {
        Refresh();
        response["request_id"] = request_id;
        client->Send(std::move(response));
    });
}

void Desktop::Tray(DWORD action)
{
    if (headless_ || !window_) return;
    NOTIFYICONDATAW icon{sizeof(icon)};
    icon.hWnd = window_; icon.uID = 1;
    icon.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP | NIF_SHOWTIP;
    icon.uCallbackMessage = tray;
    icon.hIcon = LoadIconW(GetModuleHandleW(nullptr), MAKEINTRESOURCEW(2));
    auto tooltip = Tooltip();
    wcsncpy_s(icon.szTip, tooltip.c_str(), _TRUNCATE);
    if (Shell_NotifyIconW(action, &icon) && action == NIM_ADD)
    {
        icon.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, &icon);
    }
}

void Desktop::Open()
{
    if (exiting_ || engine_->IsStopping()) return;
    reopen_ = true;
    if (ui_)
    {
        ULONG process = 0;
        if (GetNamedPipeClientProcessId(ui_->handle, &process)) AllowSetForegroundWindow(process);
        ui_->Send(Json{{"type", "activate"}});
        opened_ = GetTickCount64();
        return;
    }
    if (process_ && WaitForSingleObject(process_, 0) == WAIT_TIMEOUT) return;
    if (process_) { CloseHandle(process_); process_ = nullptr; }
    reopen_ = false;
    wchar_t location[32768];
    GetModuleFileNameW(nullptr, location, static_cast<DWORD>(std::size(location)));
    auto folder = std::filesystem::path(location).parent_path();
    auto executable = folder / L"TinyTorrent.exe";
    if (!std::filesystem::exists(executable) && folder.parent_path().filename() == L"Engine" &&
        folder.parent_path().parent_path().filename() == L"bin")
        executable = folder.parent_path().parent_path() / L"TinyTorrent" /
            (folder.filename() == L"Debug" ? L"debug_win-x64" : L"release_win-x64") / L"TinyTorrent.exe";
    std::wstring arguments = L"\"" + executable.wstring() + L"\"";
    STARTUPINFOW startup{sizeof(startup)};
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(executable.c_str(), arguments.data(), nullptr, nullptr, FALSE, 0,
        nullptr, executable.parent_path().c_str(), &startup, &process))
    {
        opened_ = 0;
        Startup("launch", executable.wstring() + L"\n" + std::to_wstring(GetLastError()));
        return;
    }
    CloseHandle(process.hThread);
    process_ = process.hProcess;
    AllowSetForegroundWindow(process.dwProcessId);
    opened_ = GetTickCount64();
    Startup();
}

void Desktop::Exit()
{
    if (exiting_ || engine_->IsStopping()) return;
    exiting_ = true;
    Power();
    if (ui_) { ui_->Send(Json{{"type", "close"}}); opened_ = GetTickCount64(); }
    else if (process_ && WaitForSingleObject(process_, 0) == WAIT_TIMEOUT)
        opened_ = GetTickCount64();
    else Shutdown();
}

void Desktop::Shutdown()
{
    if (saving_) return;
    saving_ = true;
    opened_ = 0;
    engine_->Shutdown([this](bool saved)
    {
        saving_ = false;
        if (ending_) { end_saved_ = saved; return; }
        if (saved) PostQuitMessage(0);
        else if (headless_) PostQuitMessage(1);
        else Startup("save");
    });
}

std::wstring Desktop::Format(std::string const& group, std::string const& key,
    std::vector<std::wstring> const& values) const
{
    auto text = Text(group, key);
    for (size_t index = 0; index < values.size(); ++index)
    {
        auto marker = L"{" + std::to_wstring(index) + L"}";
        size_t offset = 0;
        while ((offset = text.find(marker, offset)) != std::wstring::npos)
        {
            text.replace(offset, marker.size(), values[index]);
            offset += values[index].size();
        }
    }
    return text;
}

std::wstring Desktop::Rate(std::int64_t bytes) const
{
    double value = static_cast<double>(bytes);
    unsigned unit = 0;
    while (value >= 1024 && unit < 3) { value /= 1024; ++unit; }
    wchar_t number[64];
    swprintf_s(number, unit ? L"%.1f" : L"%.0f", value);
    wchar_t decimal[8];
    wchar_t const* locale = LOCALE_NAME_USER_DEFAULT;
    GetLocaleInfoEx(locale, LOCALE_SDECIMAL, decimal, static_cast<int>(std::size(decimal)));
    NUMBERFMTW format{unit ? 1U : 0U, 1, 0, decimal, const_cast<LPWSTR>(L""), 1};
    wchar_t formatted[64];
    if (GetNumberFormatEx(locale, 0, number, &format, formatted, static_cast<int>(std::size(formatted))))
        wcscpy_s(number, formatted);
    char const* units[] = {"bytes", "kib", "mib", "gib"};
    return Format("units", units[unit], {number});
}

std::wstring Desktop::Rates() const
{
    return Format("tray", "rates", {Rate(activity_.value("download_rate", std::int64_t{})),
        Rate(activity_.value("upload_rate", std::int64_t{}))});
}

std::wstring Desktop::Counts() const
{
    return activity_.value("all_paused", false) ?
        Format("tray", activity_.value("torrent_count", 0) == 1 ? "paused_one" : "paused",
            {std::to_wstring(activity_.value("torrent_count", 0))}) :
        Format("tray", "counts", {std::to_wstring(activity_.value("active", 0)), std::to_wstring(activity_.value("queued", 0))});
}

std::wstring Desktop::Tooltip() const
{
    if (engine_->IsLoading()) return Text("startup", "loading");
    return activity_.value("all_paused", false) ? Counts() : Rates() + L"\n" + Counts();
}

void Desktop::Pause()
{
    engine_->Execute({{"command", "session_pause"}, {"paused", !activity_.value("all_paused", false)}},
        [this](Json response)
        {
            if (!response.value("ok", false)) Notice({{"kind", "error"}, {"name", "TinyTorrent"},
                {"detail", Utf8(Text("error", "pause")) + " " + response.at("error").value("detail", "")}});
            Refresh();
        });
}

void Desktop::Menu(POINT point)
{
    SetWindowPos(broadcast_, nullptr, point.x, point.y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    auto dpi = GetDpiForWindow(broadcast_);
    NONCLIENTMETRICSW metrics{sizeof(metrics)};
    SystemParametersInfoForDpi(SPI_GETNONCLIENTMETRICS, sizeof(metrics), &metrics, 0, dpi);
    menu_font_ = CreateFontIndirectW(&metrics.lfMenuFont);
    SetForegroundWindow(broadcast_);
    UINT selected;
    for (;;)
    {
        menu_ = CreatePopupMenu();
        auto language = language_;
        AppendMenuW(menu_, MF_STRING | MF_DISABLED, 10, Rates().c_str());
        AppendMenuW(menu_, MF_STRING | MF_DISABLED, 11, Counts().c_str());
        for (UINT id : {10u, 11u})
        {
            auto index = id - 10;
            menu_text_[index] = id == 10 ? Rates() : Counts();
            menu_names_[index] = {MSAA_MENU_SIG, static_cast<DWORD>(menu_text_[index].size()), menu_text_[index].data()};
            MENUITEMINFOW row{sizeof(row)};
            row.fMask = MIIM_FTYPE | MIIM_DATA;
            row.fType = MFT_OWNERDRAW;
            row.dwItemData = reinterpret_cast<ULONG_PTR>(&menu_names_[index]);
            SetMenuItemInfoW(menu_, id, FALSE, &row);
        }
        AppendMenuW(menu_, MF_SEPARATOR, 0, nullptr);
        AppendMenuW(menu_, MF_STRING, 1, Text("tray", "open").c_str());
        AppendMenuW(menu_, MF_STRING, 3, Text("tray", activity_.value("all_paused", false) ? "resume" : "pause").c_str());
        EnableMenuItem(menu_, 3, MF_BYCOMMAND | (engine_->IsLoading() || engine_->HasStorageFailure() || exiting_ ? MF_GRAYED : MF_ENABLED));
        AppendMenuW(menu_, MF_SEPARATOR, 0, nullptr);
        AppendMenuW(menu_, MF_STRING, 2, Text("tray", "exit").c_str());
        SetMenuDefaultItem(menu_, 1, FALSE);
        selected = TrackPopupMenu(menu_, TPM_RETURNCMD | TPM_RIGHTBUTTON,
            point.x, point.y, 0, broadcast_, nullptr);
        DestroyMenu(menu_);
        menu_ = nullptr;
        if (language == language_) break;
    }
    DeleteObject(menu_font_);
    menu_font_ = nullptr;
    if (selected == 1) Open();
    else if (selected == 2) Exit();
    else if (selected == 3) Pause();
    PostMessageW(broadcast_, WM_NULL, 0, 0);
}

LRESULT Desktop::MenuRow(UINT message, LPARAM parameter)
{
    UINT id = message == WM_MEASUREITEM ? reinterpret_cast<MEASUREITEMSTRUCT*>(parameter)->itemID :
        reinterpret_cast<DRAWITEMSTRUCT*>(parameter)->itemID;
    if (id != 10 && id != 11) return FALSE;
    auto text = id == 10 ? Rates() : Counts();
    auto dpi = GetDpiForWindow(broadcast_);
    auto margin = GetSystemMetricsForDpi(SM_CXMENUCHECK, dpi) + MulDiv(8, dpi, 96);
    if (message == WM_MEASUREITEM)
    {
        auto row = reinterpret_cast<MEASUREITEMSTRUCT*>(parameter);
        auto dc = GetDC(broadcast_);
        auto previous = SelectObject(dc, menu_font_);
        SIZE size{};
        GetTextExtentPoint32W(dc, text.c_str(), static_cast<int>(text.size()), &size);
        row->itemWidth = size.cx + margin * 2;
        row->itemHeight = std::max<LONG>(size.cy + MulDiv(8, dpi, 96), GetSystemMetricsForDpi(SM_CYMENU, dpi));
        SelectObject(dc, previous);
        ReleaseDC(broadcast_, dc);
    }
    else
    {
        auto row = reinterpret_cast<DRAWITEMSTRUCT*>(parameter);
        FillRect(row->hDC, &row->rcItem, GetSysColorBrush(COLOR_MENU));
        auto previous = SelectObject(row->hDC, menu_font_);
        auto color = SetTextColor(row->hDC, GetSysColor(COLOR_MENUTEXT));
        auto background = SetBkMode(row->hDC, TRANSPARENT);
        auto bounds = row->rcItem;
        bounds.left += margin;
        DrawTextW(row->hDC, text.c_str(), static_cast<int>(text.size()), &bounds,
            DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX);
        SetBkMode(row->hDC, background);
        SetTextColor(row->hDC, color);
        SelectObject(row->hDC, previous);
    }
    return TRUE;
}

void Desktop::Startup(std::string failure, std::wstring detail)
{
    startup_failure_ = std::move(failure);
    startup_detail_ = std::move(detail);
    if (headless_)
    {
        if (!startup_failure_.empty()) Feedback(startup_failure_, startup_detail_);
        return;
    }
    if (!splash_ || !IsWindow(splash_))
    {
        POINT point{};
        GetCursorPos(&point);
        MONITORINFO monitor{sizeof(monitor)};
        GetMonitorInfoW(MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST), &monitor);
        splash_ = CreateWindowExW(WS_EX_TOOLWINDOW, L"TinyTorrent.Startup", L"TinyTorrent",
            WS_POPUP | WS_BORDER, point.x, point.y, 350, 170,
            nullptr, nullptr, GetModuleHandleW(nullptr), this);
        auto dpi = GetDpiForWindow(splash_);
        auto width = MulDiv(350, dpi, 96);
        auto height = MulDiv(170, dpi, 96);
        SetWindowPos(splash_, HWND_TOP, monitor.rcWork.left + (monitor.rcWork.right - monitor.rcWork.left - width) / 2,
            monitor.rcWork.top + (monitor.rcWork.bottom - monitor.rcWork.top - height) / 2, width, height, SWP_NOACTIVATE);
        CreateWindowW(L"BUTTON", L"", WS_CHILD | WS_TABSTOP | BS_DEFPUSHBUTTON, 0, 0, 0, 0, splash_,
            reinterpret_cast<HMENU>(1), GetModuleHandleW(nullptr), nullptr);
        CreateWindowW(L"BUTTON", L"", WS_CHILD | WS_TABSTOP | BS_PUSHBUTTON, 0, 0, 0, 0, splash_,
            reinterpret_cast<HMENU>(2), GetModuleHandleW(nullptr), nullptr);
        CreateWindowW(L"STATIC", L"", WS_CHILD | WS_VISIBLE | SS_CENTER | SS_NOPREFIX, 0, 0, 0, 0, splash_,
            reinterpret_cast<HMENU>(3), GetModuleHandleW(nullptr), nullptr);
        CreateWindowW(L"STATIC", L"", WS_CHILD | SS_ICON | SS_CENTERIMAGE, 0, 0, 0, 0, splash_,
            reinterpret_cast<HMENU>(4), GetModuleHandleW(nullptr), nullptr);
        SendMessageW(splash_, WM_DPICHANGED, dpi, 0);
        ShowWindow(splash_, SW_SHOWNOACTIVATE);
    }
    auto retry = GetDlgItem(splash_, 1);
    auto close = GetDlgItem(splash_, 2);
    SetWindowTextW(retry, Text("dialog", startup_failure_ == "unresponsive" ? "exit_anyway" : "retry").c_str());
    SetWindowTextW(close, Text("dialog", startup_failure_ == "save" ? "exit_anyway" : startup_failure_ == "unresponsive" ? "cancel" : "close").c_str());
    ShowWindow(retry, startup_failure_.empty() ? SW_HIDE : SW_SHOW);
    ShowWindow(close, startup_failure_.empty() ? SW_HIDE : SW_SHOW);
    ShowWindow(GetDlgItem(splash_, 4), startup_failure_.empty() ? SW_SHOW : SW_HIDE);
    EnableWindow(retry, startup_failure_ == "save" || startup_failure_ == "unresponsive" ||
        !process_ || WaitForSingleObject(process_, 0) != WAIT_TIMEOUT);
    auto text = startup_failure_.empty() ? Text("startup", "opening") : Text("error", startup_failure_);
    if (!startup_detail_.empty()) text += L"\n" + startup_detail_;
    SetWindowTextW(GetDlgItem(splash_, 3), text.c_str());
    auto dpi = GetDpiForWindow(splash_);
    SetWindowPos(splash_, nullptr, 0, 0, MulDiv(startup_failure_.empty() ? 350 : 440, dpi, 96),
        MulDiv(startup_failure_.empty() ? 170 : 240, dpi, 96), SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
    SendMessageW(splash_, WM_SIZE, 0, 0);
    InvalidateRect(splash_, nullptr, TRUE);
    if (!startup_failure_.empty()) SetForegroundWindow(splash_);
}

LRESULT CALLBACK Desktop::Surface(HWND window, UINT message, WPARAM first, LPARAM second)
{
    auto owner = reinterpret_cast<Desktop*>(GetWindowLongPtrW(window, GWLP_USERDATA));
    if (message == WM_NCCREATE)
    {
        owner = static_cast<Desktop*>(reinterpret_cast<CREATESTRUCTW*>(second)->lpCreateParams);
        SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(owner));
    }
    if (!owner) return DefWindowProcW(window, message, first, second);
    if (message == WM_DPICHANGED)
    {
        auto dpi = first ? LOWORD(first) : GetDpiForWindow(window);
        NONCLIENTMETRICSW metrics{sizeof(metrics)};
        SystemParametersInfoForDpi(SPI_GETNONCLIENTMETRICS, sizeof(metrics), &metrics, 0, dpi);
        if (owner->font_) DeleteObject(owner->font_);
        owner->font_ = CreateFontIndirectW(&metrics.lfMessageFont);
        if (second)
        {
            auto bounds = reinterpret_cast<RECT*>(second);
            SetWindowPos(window, nullptr, bounds->left, bounds->top, bounds->right - bounds->left,
                bounds->bottom - bounds->top, SWP_NOZORDER | SWP_NOACTIVATE);
        }
        for (int id : {1, 2, 3}) SendMessageW(GetDlgItem(window, id), WM_SETFONT, reinterpret_cast<WPARAM>(owner->font_), TRUE);
        SendMessageW(GetDlgItem(window, 4), STM_SETICON, reinterpret_cast<WPARAM>(LoadImageW(GetModuleHandleW(nullptr),
            MAKEINTRESOURCEW(2), IMAGE_ICON, MulDiv(48, dpi, 96), MulDiv(48, dpi, 96), LR_SHARED)), 0);
        SendMessageW(window, WM_SIZE, 0, 0);
        return 0;
    }
    if (message == WM_SIZE)
    {
        RECT bounds{};
        GetClientRect(window, &bounds);
        auto scale = [window](int value) { return MulDiv(value, GetDpiForWindow(window), 96); };
        for (int id : {1, 2}) SetWindowPos(GetDlgItem(window, id), nullptr,
            bounds.right - scale(id == 1 ? 194 : 102), bounds.bottom - scale(42), scale(82), scale(28), SWP_NOZORDER);
        auto status = GetDlgItem(window, 3);
        if (status)
        {
            std::wstring text(GetWindowTextLengthW(status) + 1, L'\0');
            GetWindowTextW(status, text.data(), static_cast<int>(text.size()));
            RECT measured{0, 0, bounds.right - scale(32), 0};
            auto device = GetDC(window);
            auto font = SelectObject(device, owner->font_);
            DrawTextW(device, text.c_str(), -1, &measured, DT_CALCRECT | DT_WORDBREAK | DT_NOPREFIX);
            SelectObject(device, font);
            ReleaseDC(window, device);
            auto height = bounds.bottom - (owner->startup_failure_.empty() ? 0 : scale(46));
            auto icon = owner->startup_failure_.empty() ? scale(60) : 0;
            auto top = std::max<LONG>(scale(12), (height - measured.bottom - icon) / 2);
            if (icon) SetWindowPos(GetDlgItem(window, 4), nullptr, (bounds.right - scale(48)) / 2, top,
                scale(48), scale(48), SWP_NOZORDER);
            top += icon;
            SetWindowPos(status, nullptr, scale(16), top, bounds.right - scale(32), std::min<LONG>(measured.bottom, height - top), SWP_NOZORDER);
        }
        return 0;
    }
    if (message == WM_COMMAND && (LOWORD(first) == 1 || LOWORD(first) == 2))
    {
        if (owner->startup_failure_.empty() ||
            (LOWORD(first) == 1 && !IsWindowEnabled(GetDlgItem(window, 1))) ||
            (owner->startup_failure_ == "save" && LOWORD(first) == 2 && !second)) return 0;
        auto retry = LOWORD(first) == 1;
        auto failure = owner->startup_failure_;
        DestroyWindow(window);
        owner->splash_ = nullptr;
        if (failure == "save")
        {
            if (retry) owner->Shutdown(); else PostQuitMessage(1);
        }
        else if (failure == "unresponsive")
        {
            if (retry)
            {
                auto process = owner->process_ ? GetProcessId(owner->process_) : 0;
                auto handle = process ? OpenProcess(PROCESS_TERMINATE, FALSE, process) : nullptr;
                if (handle) { TerminateProcess(handle, 1); CloseHandle(handle); }
                owner->Shutdown();
            }
            else { owner->exiting_ = false; owner->Power(); }
        }
        else if (retry) owner->Open();
        else owner->reopen_ = false;
        return 0;
    }
    if (message == WM_CLOSE)
    {
        if (!owner->startup_failure_.empty() && owner->startup_failure_ != "save")
            SendMessageW(window, WM_COMMAND, 2, 0);
        return 0;
    }
    if (message == WM_KEYDOWN && first == VK_ESCAPE && !owner->startup_failure_.empty() && owner->startup_failure_ != "save")
    { SendMessageW(window, WM_COMMAND, 2, 0); return 0; }
    if (message == WM_CTLCOLORSTATIC)
    {
        auto device = reinterpret_cast<HDC>(first);
        SetTextColor(device, GetSysColor(COLOR_WINDOWTEXT));
        SetBkColor(device, GetSysColor(COLOR_WINDOW));
        return reinterpret_cast<LRESULT>(GetSysColorBrush(COLOR_WINDOW));
    }
    if (message == WM_SETTINGCHANGE || message == WM_SYSCOLORCHANGE) InvalidateRect(window, nullptr, TRUE);
    return DefWindowProcW(window, message, first, second);
}

void Desktop::Relaunch(DWORD process)
{
    struct Target { DWORD process; HWND window = nullptr; } target{process};
    EnumWindows([](HWND window, LPARAM parameter) -> BOOL
    {
        auto& target = *reinterpret_cast<Target*>(parameter);
        DWORD process = 0;
        GetWindowThreadProcessId(window, &process);
        if (process != target.process) return TRUE;
        wchar_t name[128]{};
        GetClassNameW(window, name, 128);
        if (wcscmp(name, L"WinUIDesktopWin32WindowClass") != 0) return TRUE;
        target.window = window;
        return FALSE;
    }, reinterpret_cast<LPARAM>(&target));
    if (!target.window) return;
    wchar_t executable[32768]{};
    if (!GetModuleFileNameW(nullptr, executable, 32768)) return;
    auto initialized = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    IPropertyStore* properties = nullptr;
    auto outcome = SHGetPropertyStoreForWindow(target.window, IID_PPV_ARGS(&properties));
    if (SUCCEEDED(outcome))
    {
        auto set = [properties, &outcome](PROPERTYKEY const& key, std::wstring const& value)
        {
            if (FAILED(outcome)) return;
            PROPVARIANT property{};
            outcome = InitPropVariantFromString(value.c_str(), &property);
            if (SUCCEEDED(outcome)) outcome = properties->SetValue(key, property);
            PropVariantClear(&property);
        };
        set(PKEY_AppUserModel_RelaunchCommand, L"\"" + std::wstring(executable) + L"\"");
        set(PKEY_AppUserModel_RelaunchDisplayNameResource, L"@" + std::wstring(executable) + L",-4");
        set(PKEY_AppUserModel_RelaunchIconResource, std::wstring(executable) + L",-2");
        set(PKEY_AppUserModel_ID, L"Syno.TinyTorrent");
        if (SUCCEEDED(outcome)) outcome = properties->Commit();
        properties->Release();
    }
    if (FAILED(outcome)) OutputDebugStringW(L"TinyTorrent could not set taskbar relaunch properties.\n");
    if (SUCCEEDED(initialized)) CoUninitialize();
}

void Desktop::Notice(Json notice)
{
    auto kind = notice.value("kind", "");
    if (headless_ || ((kind == "error" || kind == "add_failed") && ui_) ||
        ((kind == "completed" || kind == "added") && !activity_.value("notifications_enabled", true))) return;
    if (kind == "add_failed" || kind == "error")
    {
        if (!failed_notices_) failed_notice_ = notice;
        ++failed_notices_;
    }
    if (notices_.size() < 32) notices_.push_back(std::move(notice));
    ++notice_count_;
    if (!notice_due_) notice_due_ = GetTickCount64() + 1000;
}

void Desktop::Notify()
{
    if (!notice_due_ || GetTickCount64() < notice_due_) return;
    auto count = notice_count_;
    notice_due_ = 0;
    notice_count_ = 0;
    notification_ = count == 1 ? std::move(notices_.front()) : Json{{"kind", "aggregate"}};
    notices_.clear();
    QUERY_USER_NOTIFICATION_STATE state{};
    if (FAILED(SHQueryUserNotificationState(&state)) || state != QUNS_ACCEPTS_NOTIFICATIONS)
    { notification_ = Json(); failed_notice_ = Json(); failed_notices_ = 0; return; }
    auto kind = notification_.value("kind", "");
    auto message = kind == "aggregate" ? Format("notification", "aggregate", {std::to_wstring(count)}) :
        kind == "background" ? Text("tray", "background") :
        Format("notification", kind, {Wide(notification_.value("name", ""))});
    auto detail = notification_.value("detail", "");
    if (!detail.empty()) message += L"\n" + Wide(detail);
    bool failed = failed_notices_ != 0;
    if (failed)
    {
        auto name = Wide(failed_notice_.value("name", ""));
        bool addition = failed_notice_.value("kind", "") == "add_failed";
        if (addition && !name.starts_with(L"magnet:")) name = std::filesystem::path(name).filename().wstring();
        message = Format("notification", "failures", {std::to_wstring(failed_notices_),
            name.substr(0, 60), Wide(failed_notice_.value("detail", "")).substr(0, 120)});
        failed_notices_ = 0;
        failed_notice_ = Json();
    }
    NOTIFYICONDATAW icon{sizeof(icon)};
    icon.hWnd = window_; icon.uID = 1; icon.uFlags = NIF_INFO | NIF_REALTIME;
    icon.dwInfoFlags = (kind == "error" || failed ? NIIF_ERROR : NIIF_INFO) | NIIF_RESPECT_QUIET_TIME;
    wcscpy_s(icon.szInfoTitle, L"TinyTorrent");
    wcsncpy_s(icon.szInfo, message.c_str(), _TRUNCATE);
    Shell_NotifyIconW(NIM_MODIFY, &icon);
}

void Desktop::Power()
{
    SYSTEM_POWER_STATUS source{};
    bool needed = !exiting_ && !engine_->IsStopping() && GetSystemPowerStatus(&source) && source.ACLineStatus == 1 &&
        activity_.value("prevent_sleep", true) && (activity_.value("downloading", false) ||
            (activity_.value("prevent_sleep_seeding", false) && activity_.value("seeding", false)));
    if (needed == awake_) return;
    if (needed)
    {
        if (power_ == INVALID_HANDLE_VALUE)
        {
            auto reason = Text("power", "transfers");
            REASON_CONTEXT context{POWER_REQUEST_CONTEXT_VERSION, POWER_REQUEST_CONTEXT_SIMPLE_STRING};
            context.Reason.SimpleReasonString = reason.data();
            power_ = PowerCreateRequest(&context);
        }
        if (power_ != INVALID_HANDLE_VALUE && PowerSetRequest(power_, PowerRequestSystemRequired)) awake_ = true;
    }
    else if (PowerClearRequest(power_, PowerRequestSystemRequired)) awake_ = false;
}

void Desktop::SessionEnd()
{
    ending_ = true;
    exiting_ = true;
    if (splash_ && IsWindow(splash_)) DestroyWindow(splash_);
    splash_ = nullptr;
    Power();
    Shutdown();
    auto deadline = GetTickCount64() + 4000;
    while (!end_saved_.has_value() && GetTickCount64() < deadline)
    {
        auto now = GetTickCount64();
        if (now >= deadline) break;
        MsgWaitForMultipleObjectsEx(0, nullptr, static_cast<DWORD>(deadline - now), QS_ALLINPUT, MWMO_INPUTAVAILABLE);
        MSG message{};
        while (GetTickCount64() < deadline && PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
        {
            if (message.message == WM_QUIT) continue;
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
    }
    if (!end_saved_.has_value()) TerminateProcess(GetCurrentProcess(), 1);
    PostQuitMessage(end_saved_.value_or(false) ? 0 : 1);
}

bool Desktop::ValidSources(Json const& sources)
{
    return sources.is_array() && !sources.empty() && sources.size() <= 256 &&
        std::all_of(sources.begin(), sources.end(), [](Json const& source)
        {
            if (!source.is_string()) return false;
            auto const& text = source.get_ref<std::string const&>();
            return !text.empty() && text.size() <= 32768 && text.find('\0') == std::string::npos;
        });
}

Json Desktop::Activate(Json const& sources)
{
    size_t count = 0;
    for (auto const& activation : waiting_) count += activation.sources.size();
    for (auto const& activation : activations_) count += activation.sources.size();
    if (!ValidSources(sources) || count + sources.size() > 256)
        return {{"ok", false}, {"error", {{"code", "invalid_sources"}, {"detail", ""}}}};
    if (exiting_ || engine_->IsStopping() || engine_->HasStorageFailure())
        return {{"ok", false}, {"error", {{"code", "unavailable"}, {"detail", ""}}}};
    auto id = std::to_string(++sequence_);
    waiting_.push_back({id, sources.get<std::vector<std::string>>()});
    PostMessageW(window_, wake, 0, 0);
    return {{"ok", true}, {"data", {{"activation_id", id}}}};
}

void Desktop::Sources()
{
    if (adding_ || waiting_.empty() || engine_->IsLoading() || exiting_ || engine_->IsStopping()) return;
    if (engine_->HasStorageFailure())
    {
        adding_ = true;
        Feedback("source", Wide(waiting_.front().sources.front()));
        waiting_.clear();
        adding_ = false;
        return;
    }
    if (ui_ || engine_->ShowsAdd())
    {
        while (!waiting_.empty())
        {
            activations_.push_back(std::move(waiting_.front()));
            waiting_.pop_front();
        }
        if (ui_) ui_->Send(Json{{"type", "sources"}});
        Open();
        return;
    }
    adding_ = true;
    auto connection = "activation." + waiting_.front().activation_id;
    auto destination = engine_->DefaultDestination();
    engine_->Execute({{"command", "preview"}, {"source", waiting_.front().sources.front()},
        {"destination", destination}, {"connection_id", connection}},
        [this, connection, destination](Json response)
        {
            if (!response.value("ok", false)) { Finish(response); return; }
            auto const& preview = response.at("data");
            if (preview.value("merge_available", false))
            {
                engine_->Disconnect(connection);
                activations_.push_back(std::move(waiting_.front()));
                waiting_.pop_front();
                adding_ = false;
                if (ui_) ui_->Send(Json{{"type", "sources"}});
                Open();
                PostMessageW(window_, wake, 0, 0);
                return;
            }
            engine_->Execute({{"command", "add"}, {"preview_id", preview.at("preview_id")},
                {"destination", destination}, {"paused", false}, {"connection_id", connection}},
                [this](Json result) { Finish(result); });
        });
}

void Desktop::Finish(Json const& response)
{
    auto& activation = waiting_.front();
    engine_->Disconnect("activation." + activation.activation_id);
    if (!response.value("ok", false))
    {
        auto detail = response.at("error").value("detail", "");
        if (headless_) Feedback("source", Wide(activation.sources.front()) + L"\n" + Wide(detail));
        else Notice({{"kind", "add_failed"}, {"name", activation.sources.front()}, {"detail", detail}});
    }
    else Notice({{"kind", "added"}, {"torrent_id", response.at("data").value("torrent_id", "")},
        {"name", activation.sources.front()}});
    activation.sources.erase(activation.sources.begin());
    if (activation.sources.empty()) waiting_.pop_front();
    adding_ = false;
    PostMessageW(window_, wake, 0, 0);
}

int Desktop::Run(bool background, Json sources)
{
    Tray(NIM_ADD);
    if (!sources.empty())
    {
        auto response = Activate(sources);
        if (!response.value("ok", false))
        {
            Feedback("source", Wide(response.at("error").dump()));
            return 1;
        }
    }
    else if (!background && !headless_) Open();
    MSG message{};
    while (GetMessageW(&message, nullptr, 0, 0) > 0)
    {
        if (!splash_ || !IsDialogMessageW(splash_, &message))
        {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
    }
    return static_cast<int>(message.wParam);
}

void Desktop::Tick()
{
    if (ticking_) return;
    ticking_ = true;
    engine_->Tick();
    Refresh();
    Sources();
    if (reopen_ && !opened_ && !ui_ && (!process_ || WaitForSingleObject(process_, 0) == WAIT_OBJECT_0))
    {
        reopen_ = false;
        Open();
    }
    for (auto& notice : engine_->TakeNotifications()) Notice(std::move(notice));
    Notify();
    Power();
    ticking_ = false;
}

void Desktop::Refresh()
{
    activity_ = engine_->Activity();
    if (menu_)
    {
        auto replace = [this](UINT id, std::wstring text)
        {
            MENUITEMINFOW item{sizeof(item)};
            item.fMask = MIIM_STRING;
            item.dwTypeData = text.data();
            if (id == 10 || id == 11)
            {
                auto index = id - 10;
                menu_text_[index] = text;
                menu_names_[index] = {MSAA_MENU_SIG, static_cast<DWORD>(text.size()), menu_text_[index].data()};
            }
            SetMenuItemInfoW(menu_, id, FALSE, &item);
        };
        replace(10, Rates());
        replace(11, Counts());
        replace(3, Text("tray", activity_.value("all_paused", false) ? "resume" : "pause"));
        EnableMenuItem(menu_, 3, MF_BYCOMMAND | (engine_->IsLoading() || engine_->HasStorageFailure() || exiting_ ? MF_GRAYED : MF_ENABLED));
    }
    auto tooltip = Tooltip();
    if (tooltip != tooltip_) { tooltip_ = tooltip; Tray(NIM_MODIFY); }
    auto language = engine_->Language();
    if (language == language_) return;
    auto strings = LoadStrings(language);
    if (menu_) EndMenu();
    language_ = std::move(language);
    strings_ = std::move(strings);
    Tray(NIM_MODIFY);
    if (splash_ && IsWindow(splash_)) Startup(startup_failure_, startup_detail_);
    if (power_ != INVALID_HANDLE_VALUE)
    {
        if (awake_) PowerClearRequest(power_, PowerRequestSystemRequired);
        CloseHandle(power_);
        power_ = INVALID_HANDLE_VALUE;
        awake_ = false;
        Power();
    }
}

LRESULT CALLBACK Desktop::Window(HWND window, UINT message, WPARAM first, LPARAM second)
{
    auto owner = reinterpret_cast<Desktop*>(GetWindowLongPtrW(window, GWLP_USERDATA));
    if (message == WM_NCCREATE)
    {
        owner = static_cast<Desktop*>(reinterpret_cast<CREATESTRUCTW*>(second)->lpCreateParams);
        owner->window_ = window;
        SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(owner));
    }
    return owner ? owner->Handle(message, first, second) : DefWindowProcW(window, message, first, second);
}

LRESULT Desktop::Handle(UINT message, WPARAM first, LPARAM second)
{
    if (message == WM_MEASUREITEM || message == WM_DRAWITEM) return MenuRow(message, second);
    if (message == dispatch)
    {
        std::deque<std::function<void()>> pending;
        { std::lock_guard lock(mutex_); pending.swap(pending_); }
        for (auto& action : pending) action();
        return 0;
    }
    if (message == explorer_) { Tray(NIM_ADD); return 0; }
    if (message == wake) { Tick(); return 0; }
    if (message == WM_TIMER)
    {
        Tick();
        if (opened_ && process_ && WaitForSingleObject(process_, 0) == WAIT_OBJECT_0 && !ui_)
        {
            if (exiting_) Shutdown();
            else
            {
                DWORD code = 0;
                GetExitCodeProcess(process_, &code);
                if (code != 0)
                {
                    opened_ = 0;
                    reopen_ = false;
                    Startup("startup", std::to_wstring(code));
                }
            }
        }
        if (opened_ && GetTickCount64() - opened_ > 30000)
        {
            opened_ = 0;
            reopen_ = false;
            if (exiting_) { if (headless_) Shutdown(); else Startup("unresponsive"); }
            else Startup("startup");
        }
        if (splash_ && IsWindow(splash_) && !startup_failure_.empty())
            EnableWindow(GetDlgItem(splash_, 1), startup_failure_ == "save" || startup_failure_ == "unresponsive" ||
                !process_ || WaitForSingleObject(process_, 0) != WAIT_TIMEOUT);
        return 0;
    }
    if (message == tray)
    {
        auto notification = LOWORD(second);
        if (notification == NIN_SELECT || notification == NIN_KEYSELECT || notification == WM_LBUTTONDBLCLK) Open();
        else if (notification == WM_RBUTTONUP || notification == WM_CONTEXTMENU)
        {
            POINT point{static_cast<short>(LOWORD(first)), static_cast<short>(HIWORD(first))};
            Menu(point);
        }
        else if (notification == NIN_BALLOONUSERCLICK)
        {
            if (!notification_.is_object()) return 0;
            auto kind = notification_.value("kind", "");
            auto folder = kind == "completed" ? engine_->Folder(notification_.value("torrent_id", "")) : std::string();
            notification_ = Json();
            if (!folder.empty()) ShellExecuteW(nullptr, L"open", Wide(folder).c_str(), nullptr, nullptr, SW_SHOWNORMAL);
            else Open();
        }
        else if (notification == NIN_BALLOONTIMEOUT) notification_ = Json();
        return 0;
    }
    if (message == WM_QUERYENDSESSION) return TRUE;
    if (message == WM_ENDSESSION) { if (first) SessionEnd(); return 0; }
    if (message == WM_POWERBROADCAST) { Tick(); return TRUE; }
    return DefWindowProcW(window_, message, first, second);
}
}
