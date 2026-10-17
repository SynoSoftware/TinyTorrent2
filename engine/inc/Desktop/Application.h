#pragma once
#include "Owned.h"
#include "Pipe.h"
#include "Registration.h"
#include "Strings.h"
#include "Desktop/Notices.h"
#include "Desktop/PowerRequest.h"
#include "Desktop/Splash.h"
#include "Desktop/Tray.h"
#include <optional>

namespace tt::desktop
{
// Why a launch cannot start, as the failure popup shows it.
struct Refusal
{
    // The explanation's key in the error group.
    std::string key;
    // One diagnostic line, such as the folder the failure concerns.
    std::wstring detail;
    // Adds Open folder for the folder that holds what the person must close.
    std::optional<std::filesystem::path> folder;
    // Adds Retry, which starts this launch again, for a failure the person
    // can resolve without leaving the popup.
    bool retry = false;
};

class Application
{
public:
    Application(std::filesystem::path directory, std::wstring sid, bool headless);
    ~Application() = default;
    int Run(bool background, std::vector<std::string> sources = {});
#ifdef _DEBUG
    // Shows the popup of an Added notice for a sample torrent once the engine
    // has loaded, whatever the notice switches say.
    void ShowSampleNotice();
#endif
    static bool ValidSources(std::vector<std::string> const& sources);
    // Shows why a launch cannot start in the failure popup, without an engine
    // of its own, and returns once the person closes it.
    static void ShowRefusal(Strings const& strings, Refusal refusal);
private:
    struct Activation
    {
        std::string activationId;
        bool batch;
        std::vector<std::string> sources;
    };
    static LRESULT CALLBACK Procedure(HWND window, UINT message, WPARAM first, LPARAM second);
    LRESULT Handle(UINT message, WPARAM first, LPARAM second);
    void OnTray(WPARAM first, LPARAM second);
    void TurnOff(std::vector<NoticeSwitch> notices);
    void ShowMenu(POINT point);
    void OnTimer();
    void Dispatch(std::shared_ptr<Pipe::Connection> client, Json request, Reply reply);
    void Receive(std::shared_ptr<Pipe::Connection> const& client, Json const& request, Reply const& reply);
    void OnReady(std::shared_ptr<Pipe::Connection> const& client, Reply const& reply);
    void OnClosed(Reply const& reply);
    void OnActivateReply(std::shared_ptr<Pipe::Connection> const& client, bool available, Reply const& reply);
    void OnCloseReply(std::shared_ptr<Pipe::Connection> const& client, CloseState state, Reply const& reply);
    void OnExitReply(std::shared_ptr<Pipe::Connection> const& client, ExitAnswer answer, Reply const& reply);
    void OnPendingActivations(std::shared_ptr<Pipe::Connection> const& client, Reply const& reply);
    void OnActivationsReceived(std::shared_ptr<Pipe::Connection> const& client, std::vector<std::string> const& ids,
        Reply const& reply);
    void Disconnect(std::shared_ptr<Pipe::Connection> const& client);
    void ForgetWindow();
    bool Open();
    bool IsWindowRunning() const;
    // The launched window when it is on screen without a connection to this
    // engine, showing its own failure, such as an engine of another version.
    HWND ShownWindow() const;
    void Exit();
    bool ConfirmExit();
    void BeginExit();
    void Shutdown();
    bool IsExiting() const;
    void CancelExit();
    void EndSession();
    void Tick();
    void Refresh();
    void ShowSplash();
    void Pause();
    Json Activate(std::vector<std::string> sources);
    void AddSources();
    void FinishSource(Outcome const& outcome, Added const& added);
    void CheckPrograms();
    // Starts or raises the window at `card`, a card on the General page of
    // Settings, which the window highlights.
    void OpenSettings(std::string card);
    void ShowSettings();
    void Notify(Notice notice);
    void ShowError(std::string const& key, std::wstring detail = {}, std::vector<Popup::Action> actions = {});
    void ShowStartError(std::string const& key, std::wstring detail = {});
    void ShowSaveFailure();
    bool headless_;
    ExitPhase exit_ = ExitPhase::Idle;
    bool ticking_ = false;
    bool adding_ = false;
    bool reopen_ = false;
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
    // The Settings card to show once the window connects.
    std::optional<std::string> settingsCard_;
    unsigned sequence_ = 0;
    ULONGLONG waitingSince_ = 0;
    // Cold when the current wait for a window began while the engine loaded.
    Launch launch_ = Launch::Warm;
    // When the warm launch now waiting for `ready` started its window process.
    ULONGLONG launchedAt_ = 0;
    TrayClick trayClick_ = TrayClick::Idle;
    POINT trayPoint_{};
    UINT taskbarCreated_ = RegisterWindowMessageW(L"TaskbarCreated");
    Activity activity_;
    Strings strings_;
    Splash splash_;
    PowerRequest power_;
    Registration registration_;
    std::shared_ptr<Pipe::Connection> windowClient_;
    std::deque<Activation> incoming_;
    std::deque<Activation> offered_;
    std::mutex mutex_;
    std::deque<std::function<void()>> requests_;
    // Members are released in reverse order, so the last one goes first: the
    // pipe, whose dispatch uses mutex_ and requests_, then the engine, then the
    // notices, the tray and the windows that they use.
    OwnedHandle ownership_;
    OwnedWindow owner_;
    OwnedWindow broadcast_;
    OwnedIcon errorIcon_;
    std::unique_ptr<Tray> tray_;
    std::unique_ptr<Notices> notices_;
    OwnedHandle process_;
    std::unique_ptr<Engine> engine_;
    std::unique_ptr<Pipe> pipe_;
    OwnedNotification powerNotification_;
};
}
