#pragma once
#include "Owned.h"
#include "Pipe.h"
#include "Registration.h"
#include "Strings.h"
#include "Desktop/Instance.h"
#include "Desktop/Toasts.h"
#include "Desktop/PowerRequest.h"
#include "Desktop/Tray.h"
#include "Desktop/Window.h"
#include <optional>
#include <thread>

namespace tt::desktop
{
class Application
{
    // Ends the process 10 seconds after Exit starts, also when a thread waits
    // on a drive that stopped responding; cancelling Exit stops it. After the
    // final save it ends the teardown sooner. The first member, so it is
    // released last, and it still ends a teardown that runs out of time.
    std::jthread watchdog_;

public:
    Application(std::filesystem::path directory, std::wstring sid, bool headless);
    ~Application() = default;
    int Run(bool background, std::vector<std::string> sources = {});
#ifdef _DEBUG
    // Shows the popup of an Added toast for a sample torrent once the engine
    // has loaded, whatever the toast switches say.
    void ShowSampleToast();
#endif
private:
    static LRESULT CALLBACK Procedure(HWND window, UINT message, WPARAM first, LPARAM second);
    LRESULT Handle(UINT message, WPARAM first, LPARAM second);
    // Retries every torrent that waits for the person's Retry.
    void RetryAll();
    void TurnOff(std::vector<ToastSwitch> toasts);
    void OnTimer();
    void Dispatch(std::shared_ptr<Pipe::Connection> client, Json request, Reply reply);
    void Receive(std::shared_ptr<Pipe::Connection> const& client, Json const& request, Reply const& reply);
    void Exit();
    void Answer(ExitAnswer answer);
    bool ConfirmExit();
    void BeginExit();
    void StartWatchdog();
    void Shutdown();
    bool IsExiting() const;
    void CancelExit();
    void EndSession();
    void Tick();
    void Refresh();
    void Pause();
    Json Activate(std::vector<std::string> sources);
    void AddSources();
    void FinishSource(Outcome const& outcome, Added const& added);
    void CheckPrograms();
    bool headless_;
    ExitPhase exit_ = ExitPhase::Idle;
    // When the watchdog ends an Exit that has not ended.
    std::chrono::steady_clock::time_point exitDeadline_;
    bool ticking_ = false;
    bool adding_ = false;
    // A plain start opens the window once startup has read whether the person
    // chose to start in the tray.
    bool pendingStart_ = false;
    bool endSaved_ = false;
    bool programsChecked_ = false;
    bool unfinishedNotified_ = false;
#ifdef _DEBUG
    bool showsSample_ = false;
#endif
    unsigned sequence_ = 0;
    Activity activity_;
    Strings strings_;
    PowerRequest power_;
    Registration registration_;
    std::deque<Activation> incoming_;
    std::mutex mutex_;
    std::deque<std::function<void()>> requests_;
    // Members are released in reverse order, so the last one goes first: the
    // power notification, which owner_ receives, then the pipe, whose dispatch
    // uses mutex_ and requests_, then the product window, the engine, the
    // toasts, the tray and the windows that they use.
    OwnedHandle ownership_;
    OwnedWindow owner_;
    OwnedWindow broadcast_;
    OwnedIcon errorIcon_;
    std::unique_ptr<Tray> tray_;
    std::unique_ptr<Toasts> toasts_;
    std::unique_ptr<Engine> engine_;
    std::unique_ptr<Window> window_;
    std::unique_ptr<Pipe> pipe_;
    OwnedNotification powerNotification_;
};
}
