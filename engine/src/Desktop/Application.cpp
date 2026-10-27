#include "Desktop/Application.h"
#include "Registration.h"
#include "Resources.h"
#include <algorithm>
#include <condition_variable>
#include <mutex>
#include <stdexcept>

namespace tt::desktop
{
namespace
{
constexpr UINT dispatch = WM_APP + 1;
constexpr UINT wake = WM_APP + 2;
constexpr UINT_PTR tickTimer = 1;

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
#ifdef _DEBUG
    {"sample_toast", Command::SampleToast},
#endif
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
// How long session end waits for the final save before the process ends.
constexpr ULONGLONG sessionEndTimeout = 4000;
// How long Exit may take before the watchdog ends the process. A normal Exit
// ends well within it.
constexpr auto exitLimit = std::chrono::seconds(10);
// How long libtorrent's teardown and the engine's end may take after the
// final save. The teardown waits up to a second for trackers; one that takes
// longer waits on a drive that stopped responding.
constexpr auto teardownLimit = std::chrono::seconds(2);

// Pipe requests that wait for this thread; more are refused as overloaded.
constexpr std::size_t requestLimit = 128;

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

// Ends the process with `code` at `deadline`, unless the thread is stopped
// first, as replacing or releasing it does.
std::jthread Watchdog(std::chrono::steady_clock::time_point deadline, UINT code)
{
    return std::jthread([deadline, code](std::stop_token stop)
    {
        std::mutex gate;
        std::condition_variable_any ended;
        std::unique_lock lock(gate);
        ended.wait_until(lock, stop, deadline, [] { return false; });
        if (!stop.stop_requested())
        {
            TerminateProcess(GetCurrentProcess(), code);
        }
    });
}
}

Application::Application(std::filesystem::path directory, std::wstring sid, bool headless)
    : headless_(headless),
      power_(strings_)
{
    auto module = GetModuleHandleW(nullptr);
    std::filesystem::create_directories(directory);
    directory = std::filesystem::canonical(directory);
    auto file = CreateFileW((directory / L"ownership.lock").c_str(), GENERIC_READ | GENERIC_WRITE,
        0, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        throw Refusal{.key = "ownership", .detail = directory.wstring()};
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
    if (!headless_)
    {
        errorIcon_.reset(ErrorIcon());
    }
    tray_ = std::make_unique<Tray>(owner_.get(), broadcast_.get(), strings_, headless_, errorIcon_.get(),
        Tray::Commands{
            .open = [this] { window_->Open(); },
            .pause = [this] { Pause(); },
            .exit = [this] { Exit(); },
            .refresh = [this] { Refresh(); }});
    toasts_ = std::make_unique<Toasts>(strings_, headless_, errorIcon_.get(), Toasts::Commands{
        .open = [this] { window_->Open(); },
        .openTorrent = [this](std::string torrentId) { window_->OpenTorrent(std::move(torrentId)); },
        .retryAll = [this] { RetryAll(); },
        .openSettings = [this](std::string card) { window_->OpenSettings(std::move(card)); },
        .folder = [this](std::string const& torrentId) { return engine_->Folder(torrentId); },
        .turnOff = [this](std::vector<ToastSwitch> toasts) { TurnOff(std::move(toasts)); },
        .window = [this](Toast const& toast) { return window_->Notify(toast); }});
    auto data = directory.wstring();
    engine_ = std::make_unique<Engine>(std::move(directory),
        [this] { PostMessageW(owner_.get(), wake, 0, 0); });
    window_ = std::make_unique<Window>(strings_, *engine_, *toasts_, headless_, Window::Commands{
        .shutdown = [this] { Shutdown(); },
        .cancelExit = [this] { CancelExit(); },
        .answer = [this](ExitAnswer answer) { Answer(answer); }});
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
}

#ifdef _DEBUG
void Application::ShowSampleToast()
{
    showsSample_ = true;
}
#endif

int Application::Run(bool background, std::vector<std::string> sources)
{
    tray_->Add();
    Refresh();
    if (!sources.empty())
    {
        auto response = Activate(std::move(sources));
        if (!response.value("ok", false))
        {
            toasts_->Report({.code = "source", .detail = Wide(response.at("error").dump()), .interactive = true});
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
    // The window is destroyed after the tray and the other members that
    // Handle reads, so its destruction messages must not reach Handle.
    if (message == WM_DESTROY)
    {
        SetWindowLongPtrW(window, GWLP_USERDATA, 0);
        application = nullptr;
    }
    if (!application)
    {
        return DefWindowProcW(window, message, first, second);
    }
    return application->Handle(message, first, second);
}

LRESULT Application::Handle(UINT message, WPARAM first, LPARAM second)
{
    // The window receives messages while it is created, before the tray exists.
    if (auto result = tray_ ? tray_->Handle(message, first, second) : std::nullopt)
    {
        return *result;
    }
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
    case WM_TIMER:
        if (first == tickTimer)
        {
            OnTimer();
        }
        return 0;
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
    return DefWindowProcW(owner_.get(), message, first, second);
}

void Application::RetryAll()
{
    engine_->RetryAll([this](Outcome outcome)
    {
        if (outcome.error)
        {
            toasts_->Report({.code = "retry", .detail = Wide(outcome.detail), .interactive = true});
        }
        Refresh();
    });
}

void Application::TurnOff(std::vector<ToastSwitch> toasts)
{
    engine_->TurnOff(toasts, [this](Outcome outcome)
    {
        if (outcome.error)
        {
            toasts_->Report({.code = "notification", .detail = Wide(outcome.detail), .interactive = true});
        }
        Refresh();
    });
}

void Application::OnTimer()
{
    Tick();
    window_->OnTimer();
}

void Application::Dispatch(std::shared_ptr<Pipe::Connection> client, Json request, Reply reply)
{
    std::lock_guard lock(mutex_);
    if (requests_.size() >= requestLimit && !request.is_null())
    {
        reply(Failure(ErrorCode::Overloaded));
        return;
    }
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
        engine_->Disconnect(client->connectionId);
        window_->Disconnect(client);
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
    if (command == Command::WindowClosed && !window_->Owns(client))
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
        {
            auto action = Parse(registrationActions, request.at("action").get<std::string>());
            if (!action)
            {
                reply(Failure(ErrorCode::InvalidRequest, "Unknown registration action."));
                break;
            }
            auto outcome = registration_.Execute(*action);
            // A failure also reports what Windows holds, which it kept.
            auto response = outcome.error ? Failure(*outcome.error, outcome.detail) : Success();
            response["data"] = registration_.Observe();
            reply(std::move(response));
            break;
        }
        case Command::Ready:
            window_->OnReady(client, reply);
            break;
        case Command::WindowClosed:
            window_->OnClosed(reply);
            break;
        case Command::ActivateReply:
            window_->OnActivateReply(client, request.at("available").get<bool>(), reply);
            break;
        case Command::CloseReply:
        {
            auto state = Parse(closeStates, request.at("state").get<std::string>());
            if (!state)
            {
                reply(Failure(ErrorCode::InvalidRequest));
                break;
            }
            window_->OnCloseReply(client, *state, reply);
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
            window_->OnExitReply(client, *answer, reply);
            break;
        }
        case Command::ActivateSources:
            reply(Activate(request.at("sources").get<std::vector<std::string>>()));
            break;
        case Command::PendingActivations:
            window_->OnPendingActivations(client, reply);
            break;
        case Command::ActivationsReceived:
            window_->OnActivationsReceived(client,
                request.value("activation_ids", std::vector<std::string>{}), reply);
            break;
        case Command::Open:
            if (!window_->Open())
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
#ifdef _DEBUG
        case Command::SampleToast:
            ShowSampleToast();
            reply(Success());
            break;
#endif
        }
    }
    catch (Json::exception const& error)
    {
        reply(Failure(ErrorCode::InvalidRequest, error.what()));
    }
}

void Application::Exit()
{
    if (engine_->IsShuttingDown())
    {
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
    if (window_->Confirm())
    {
        exit_ = ExitPhase::WindowConfirming;
        return;
    }
    if (ConfirmExit())
    {
        BeginExit();
    }
}

void Application::Answer(ExitAnswer answer)
{
    exit_ = ExitPhase::Idle;
    if (engine_->IsShuttingDown())
    {
        return;
    }
    if (answer == ExitAnswer::Confirmed || (answer == ExitAnswer::Unavailable && ConfirmExit()))
    {
        BeginExit();
    }
}

bool Application::ConfirmExit()
{
    bool confirmed = false;
    exit_ = ExitPhase::Confirming;
    // Lucide's power glyph, as the tray's Exit shows.
    toasts_->Ask({.title = strings_.Text("exit", "title"), .body = strings_.Text("exit", "active"),
        .answer = {.glyph = L'\uE140', .label = strings_.Text("tray", "exit"),
            .run = [&confirmed] { confirmed = true; }, .automationId = L"Exit"}});
    MSG message{};
    while (toasts_->IsOpen() && exit_ == ExitPhase::Confirming && GetMessageW(&message, nullptr, 0, 0) > 0)
    {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
    if (message.message == WM_QUIT)
    {
        PostQuitMessage(static_cast<int>(message.wParam));
    }
    toasts_->Close();
    // Windows can end the session while the prompt is open.
    if (exit_ != ExitPhase::Confirming)
    {
        return false;
    }
    exit_ = ExitPhase::Idle;
    return confirmed && !engine_->IsShuttingDown();
}

void Application::BeginExit()
{
    StartWatchdog();
    exit_ = ExitPhase::Exiting;
    auto closing = window_->Close();
    Refresh();
    if (!closing)
    {
        Shutdown();
    }
}

void Application::StartWatchdog()
{
    if (watchdog_.joinable())
        return;
    exitDeadline_ = std::chrono::steady_clock::now() + exitLimit;
    watchdog_ = Watchdog(exitDeadline_, 2);
}

void Application::Shutdown()
{
    StartWatchdog();
    engine_->Shutdown([this](ShutdownEnd end)
    {
        // A save failed, or work that may never return still runs. The saved
        // document holds the work that remains.
        if (end != ShutdownEnd::Saved)
        {
            tray_->Remove();
            TerminateProcess(GetCurrentProcess(), end == ShutdownEnd::Cut ? 0 : 1);
        }
        // Everything is saved, so a teardown that waits on a drive that
        // stopped responding ends with the process.
        auto teardownEnd = std::chrono::steady_clock::now() + teardownLimit;
        if (teardownEnd < exitDeadline_)
        {
            watchdog_ = Watchdog(teardownEnd, 0);
        }
        endSaved_ = true;
        if (exit_ == ExitPhase::SessionEnding)
        {
            return;
        }
        toasts_->Close();
        tray_->Remove();
        PostQuitMessage(0);
    });
}

// Windows is signing out or shutting down. Saves within the time Windows
// allows, and ends the process when the save does not finish in time.
void Application::EndSession()
{
    // An Exit that already saved everything ends the process by itself.
    if (endSaved_)
    {
        return;
    }
    exit_ = ExitPhase::SessionEnding;
    window_->End();
    Refresh();
    Shutdown();
    auto deadline = GetTickCount64() + sessionEndTimeout;
    while (!endSaved_)
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
    if (!endSaved_)
    {
        TerminateProcess(GetCurrentProcess(), 1);
    }
    PostQuitMessage(0);
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
#ifdef _DEBUG
    // The engine loads the transfers on this thread, and a popup shown before
    // that ends stops responding until it does.
    if (showsSample_ && !engine_->IsLoading())
    {
        showsSample_ = false;
        toasts_->ShowSample();
    }
#endif
    if (pendingStart_ && engine_->HasSettings())
    {
        pendingStart_ = false;
        if (!engine_->StartsInTray())
        {
            window_->Open();
        }
    }
    window_->Reopen();
    if (!programsChecked_ && !headless_ && !engine_->IsLoading())
    {
        programsChecked_ = true;
        CheckPrograms();
    }
    // The window asks about the unfinished work; without one, a notice
    // leads the person to it.
    if (!unfinishedNotified_ && !engine_->IsLoading() && engine_->AwaitsAnswer())
    {
        unfinishedNotified_ = true;
        if (!window_->IsPresent())
            toasts_->Queue({.kind = ToastKind::Unfinished});
    }
    for (auto& toast : engine_->TakeToasts())
    {
        toasts_->Queue(std::move(toast));
    }
    toasts_->Notify();
    ticking_ = false;
}

bool Application::IsExiting() const
{
    return exit_ == ExitPhase::Exiting || exit_ == ExitPhase::SessionEnding;
}

// Windows ending the session cannot be cancelled.
void Application::CancelExit()
{
    if (exit_ == ExitPhase::Exiting || exit_ == ExitPhase::WindowConfirming)
    {
        exit_ = ExitPhase::Idle;
        // Replacing the watchdog stops it and waits for its thread.
        if (!engine_->IsShuttingDown())
            watchdog_ = std::jthread();
    }
    Refresh();
}

// Brings the tray, the toasts, the power request, the splash theme and the
// text language in line with the engine and with Exit.
void Application::Refresh()
{
    activity_ = engine_->Activity();
    bool loading = engine_->IsLoading();
    tray_->Update(activity_, loading, !loading && !engine_->HasStorageFailure() && !IsExiting());
    toasts_->Update(activity_);
    power_.Update(activity_, IsExiting() || engine_->IsShuttingDown());
    window_->Refresh(activity_.theme);
    auto language = engine_->Language();
    if (language == strings_.Language())
    {
        return;
    }
    strings_ = Strings(language);
    tray_->Translate();
    window_->Translate();
    power_.Translate();
}

void Application::Pause()
{
    engine_->PauseSession(!activity_.pausedByChoice, [this](Outcome outcome)
    {
        if (outcome.error)
        {
            toasts_->Report({.code = "pause", .detail = Wide(outcome.detail), .interactive = true});
        }
        Refresh();
    });
}

// Accepts sources from a second launch or from the shell. They wait until
// AddSources hands them to the window or adds them directly.
Json Application::Activate(std::vector<std::string> sources)
{
    auto count = window_->Offered();
    for (auto const& activation : incoming_)
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
    incoming_.push_back({id, sources.size() > 1, std::move(sources)});
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
                toasts_->Queue({.kind = ToastKind::AddFailed, .name = source, .detail = detail});
            }
        }
        incoming_.clear();
        return;
    }
    if (!incoming_.front().batch && engine_->ShowsAdd(incoming_.front().sources.front()))
    {
        window_->Offer(std::move(incoming_.front()));
        incoming_.pop_front();
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
        // The toast shows the code's sentence, then the detail when there
        // is one.
        toasts_->Queue({.kind = ToastKind::AddFailed, .name = source, .detail = outcome.detail,
            .code = ToString(*outcome.error)});
    }
    else
    {
        auto kind = added.kind == AdditionKind::Duplicate ? ToastKind::Duplicate : ToastKind::Added;
        toasts_->Queue({.kind = kind, .name = engine_->Name(added.torrentId), .torrentId = added.torrentId});
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
    if (!activity_.toasts.contains(ToastSwitch::Problems))
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
    Toast toast{.kind = ToastKind::MissingProgram, .name = fresh.front(),
        .count = static_cast<unsigned>(fresh.size())};
    engine_->RecordPrograms(std::move(reported), [this, toast](Outcome outcome)
    {
        if (!outcome.error)
        {
            toasts_->Queue(toast);
        }
    });
}

}
