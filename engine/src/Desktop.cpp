#include "Desktop.h"
#include <algorithm>
#include <sddl.h>
#include <shellapi.h>
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
                Json{{"type", "hello"}, {"version", 1}, {"session_id", engine_->Snapshot()["session_id"]}},
                [this](Pipe::Client client, Json request) { Dispatch(std::move(client), std::move(request)); });
        }
        catch (...) { LocalFree(descriptor); throw; }
        LocalFree(descriptor);
        explorer_ = RegisterWindowMessageW(L"TaskbarCreated");
        SetTimer(window_, 1, 1000, nullptr);
        RegisterApplicationRestart(L"--background", RESTART_NO_PATCH | RESTART_NO_REBOOT);
    }
    catch (...)
    {
        if (broadcast_) DestroyWindow(broadcast_);
        if (window_) DestroyWindow(window_);
        CloseHandle(ownership_);
        ownership_ = INVALID_HANDLE_VALUE;
        throw;
    }
}

Desktop::~Desktop()
{
    engine_.reset();
    pipe_.reset();
    if (process_) CloseHandle(process_);
    Tray(NIM_DELETE);
    if (splash_ && IsWindow(splash_)) DestroyWindow(splash_);
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
            if (exiting_) Shutdown();
        }
        return;
    }
    auto command = request.at("command").get<std::string>();
    auto reply = [&] { client->Send(Json{{"request_id", request["request_id"]}, {"ok", true}, {"data", Json::object()}}); };
    if (command == "activate_sources")
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
            if (activations_.empty()) reopen_ = false;
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
        opened_ = 0;
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
        reply();
        if (exiting_) Shutdown();
        else if (!headless_ && !notified_)
        {
            notified_ = true;
            NOTIFYICONDATAW icon{sizeof(icon)};
            icon.hWnd = window_; icon.uID = 1; icon.uFlags = NIF_INFO;
            wcscpy_s(icon.szInfoTitle, L"TinyTorrent");
            wcscpy_s(icon.szInfo, Text("tray", "background").c_str());
            Shell_NotifyIconW(NIM_MODIFY, &icon);
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
    wcscpy_s(icon.szTip, Text("tray", "tooltip").c_str());
    if (Shell_NotifyIconW(action, &icon) && action == NIM_ADD)
    {
        icon.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, &icon);
    }
}

void Desktop::Open()
{
    if (exiting_ || engine_->IsStopping()) return;
    if (ui_)
    {
        ULONG process = 0;
        if (GetNamedPipeClientProcessId(ui_->handle, &process)) AllowSetForegroundWindow(process);
        ui_->Send(Json{{"type", "activate"}});
        return;
    }
    if (process_ && WaitForSingleObject(process_, 0) == WAIT_TIMEOUT) return;
    if (process_) { CloseHandle(process_); process_ = nullptr; }
    wchar_t location[32768];
    GetModuleFileNameW(nullptr, location, static_cast<DWORD>(std::size(location)));
    auto folder = std::filesystem::path(location).parent_path();
    auto executable = folder / L"TinyTorrent.exe";
    if (!std::filesystem::exists(executable))
        executable = folder.parent_path().parent_path() / L"TinyTorrent" / L"release_win-x64" / L"TinyTorrent.exe";
    std::wstring arguments = L"\"" + executable.wstring() + L"\"";
    STARTUPINFOW startup{sizeof(startup)};
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(executable.c_str(), arguments.data(), nullptr, nullptr, FALSE, 0,
        nullptr, executable.parent_path().c_str(), &startup, &process))
    {
        Feedback("launch", executable.wstring() + L"\n" + std::to_wstring(GetLastError()));
        return;
    }
    CloseHandle(process.hThread);
    process_ = process.hProcess;
    AllowSetForegroundWindow(process.dwProcessId);
    opened_ = GetTickCount64();
    if (!headless_)
    {
        auto status = Text("startup", "opening");
        splash_ = CreateWindowExW(WS_EX_TOOLWINDOW, L"STATIC", status.c_str(),
            WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_VISIBLE | SS_CENTER,
            (GetSystemMetrics(SM_CXSCREEN) - 320) / 2,
            (GetSystemMetrics(SM_CYSCREEN) - 110) / 2, 320, 110,
            nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
        auto icon = LoadIconW(GetModuleHandleW(nullptr), MAKEINTRESOURCEW(2));
        SendMessageW(splash_, WM_SETICON, ICON_BIG, reinterpret_cast<LPARAM>(icon));
        SendMessageW(splash_, WM_SETICON, ICON_SMALL, reinterpret_cast<LPARAM>(icon));
    }
}

void Desktop::Exit()
{
    if (exiting_ || engine_->IsStopping()) return;
    exiting_ = true;
    if (ui_) { ui_->Send(Json{{"type", "close"}}); opened_ = GetTickCount64(); }
    else if (process_ && WaitForSingleObject(process_, 0) == WAIT_TIMEOUT)
        opened_ = GetTickCount64();
    else Shutdown();
}

void Desktop::Shutdown()
{
    opened_ = 0;
    engine_->Shutdown([this](bool saved)
    {
        if (saved) PostQuitMessage(0);
        else
        {
            auto choice = headless_ ? IDNO : MessageBoxW(window_, Text("error", "save").c_str(),
                L"TinyTorrent", MB_RETRYCANCEL | MB_ICONERROR);
            if (choice == IDRETRY) Shutdown();
            else PostQuitMessage(1);
        }
    });
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
        reopen_ = ui_ || (process_ && WaitForSingleObject(process_, 0) == WAIT_TIMEOUT);
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
                reopen_ = ui_ || (process_ && WaitForSingleObject(process_, 0) == WAIT_TIMEOUT);
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
        Feedback("source", Wide(activation.sources.front()) + L"\n" + Wide(response.at("error").dump()));
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
        TranslateMessage(&message);
        DispatchMessageW(&message);
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
    if (reopen_ && !ui_ && (!process_ || WaitForSingleObject(process_, 0) == WAIT_OBJECT_0))
    {
        reopen_ = false;
        Open();
    }
    ticking_ = false;
}

void Desktop::Refresh()
{
    auto language = engine_->Language();
    if (language == language_) return;
    auto strings = LoadStrings(language);
    if (menu_) EndMenu();
    language_ = std::move(language);
    strings_ = std::move(strings);
    Tray(NIM_MODIFY);
    if (splash_ && IsWindow(splash_)) SetWindowTextW(splash_, Text("startup", "opening").c_str());
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
            opened_ = 0;
            if (splash_ && IsWindow(splash_)) DestroyWindow(splash_);
            splash_ = nullptr;
            if (exiting_) Shutdown();
            else Feedback("startup");
        }
        if (opened_ && GetTickCount64() - opened_ > 30000)
        {
            opened_ = 0;
            if (splash_ && IsWindow(splash_)) DestroyWindow(splash_);
            splash_ = nullptr;
            if (exiting_)
            {
                auto choice = MessageBoxW(window_, Text("error", "unresponsive").c_str(),
                    L"TinyTorrent", MB_YESNO | MB_ICONWARNING);
                if (choice == IDYES)
                {
                    ULONG process = 0;
                    if (ui_ && GetNamedPipeClientProcessId(ui_->handle, &process))
                    {
                        auto handle = OpenProcess(PROCESS_TERMINATE, FALSE, process);
                        if (handle) { TerminateProcess(handle, 1); CloseHandle(handle); }
                    }
                    else if (process_) TerminateProcess(process_, 1);
                    Shutdown();
                }
                else exiting_ = false;
            }
            else Feedback("startup");
        }
        return 0;
    }
    if (message == tray)
    {
        auto notification = LOWORD(second);
        if (notification == NIN_SELECT || notification == NIN_KEYSELECT || notification == WM_LBUTTONDBLCLK) Open();
        else if (notification == WM_RBUTTONUP || notification == WM_CONTEXTMENU)
        {
            POINT point{static_cast<short>(LOWORD(first)), static_cast<short>(HIWORD(first))};
            SetForegroundWindow(broadcast_);
            UINT selected;
            for (;;)
            {
                menu_ = CreatePopupMenu();
                auto language = language_;
                AppendMenuW(menu_, MF_STRING, 1, Text("tray", "open").c_str());
                AppendMenuW(menu_, MF_STRING, 2, Text("tray", "exit").c_str());
                selected = TrackPopupMenu(menu_, TPM_RETURNCMD | TPM_RIGHTBUTTON,
                    point.x, point.y, 0, broadcast_, nullptr);
                DestroyMenu(menu_);
                menu_ = nullptr;
                if (language == language_) break;
            }
            if (selected == 1) Open(); else if (selected == 2) Exit();
            PostMessageW(broadcast_, WM_NULL, 0, 0);
        }
        return 0;
    }
    if (message == WM_QUERYENDSESSION) { Shutdown(); return TRUE; }
    return DefWindowProcW(window_, message, first, second);
}
}
