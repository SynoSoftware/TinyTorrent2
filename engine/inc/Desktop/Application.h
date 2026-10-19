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

namespace tt::desktop
{
class Application
{
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
    void TurnOff(std::vector<ToastSwitch> toasts);
    void OnTimer();
    void Dispatch(std::shared_ptr<Pipe::Connection> client, Json request, Reply reply);
    void Receive(std::shared_ptr<Pipe::Connection> const& client, Json const& request, Reply const& reply);
    void Exit();
    void Answer(ExitAnswer answer);
    bool ConfirmExit();
    void BeginExit();
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
    void ShowSaveFailure();
    bool headless_;
    ExitPhase exit_ = ExitPhase::Idle;
    bool ticking_ = false;
    bool adding_ = false;
    // A plain start opens the window once startup has read whether the person
    // chose to start in the tray.
    bool pendingStart_ = false;
    std::optional<bool> endSaved_;
    // Why the final save failed most recently, which Exit shows again while
    // the engine keeps saving.
    std::optional<std::wstring> saveFailure_;
    bool programsChecked_ = false;
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
