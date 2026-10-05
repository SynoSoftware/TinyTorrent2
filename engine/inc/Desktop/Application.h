#pragma once
#include "Owned.h"
#include "Pipe.h"
#include "Registration.h"
#include "Strings.h"
#include "Desktop/PowerRequest.h"
#include "Desktop/Splash.h"
#include "Desktop/Tray.h"
#include <optional>

namespace tt::desktop
{
class Application
{
public:
    Application(std::filesystem::path directory, std::wstring sid, bool headless);
    ~Application() = default;
    int Run(bool background, std::vector<std::string> sources = {});
    static bool ValidSources(std::vector<std::string> const& sources);
private:
    struct Activation
    {
        std::string activationId;
        std::vector<std::string> sources;
    };
    static LRESULT CALLBACK Procedure(HWND window, UINT message, WPARAM first, LPARAM second);
    LRESULT Handle(UINT message, WPARAM first, LPARAM second);
    void OnTray(WPARAM first, LPARAM second);
    void ShowMenu(POINT point);
    void OnTimer();
    void Dispatch(Pipe::Client client, Json request, Reply reply);
    void Receive(Pipe::Client const& client, Json const& request, Reply const& reply);
    void OnReady(Pipe::Client const& client, Reply const& reply);
    void OnClosed(Reply const& reply);
    void OnActivateReply(Pipe::Client const& client, bool available, Reply const& reply);
    void OnCloseReply(Pipe::Client const& client, CloseState state, Reply const& reply);
    void OnPendingSources(Pipe::Client const& client, Reply const& reply);
    void OnSourcesReceived(Pipe::Client const& client, std::vector<std::string> const& ids,
        Reply const& reply);
    void Disconnect(Pipe::Client const& client);
    void ForgetWindow();
    void ExplainBackground();
    bool Open();
    bool IsWindowRunning() const;
    void Exit();
    void Shutdown();
    void CancelExit();
    void EndSession();
    void Tick();
    void Refresh();
    void Pause();
    Json Activate(std::vector<std::string> sources);
    void AddSources();
    void FinishSource(Outcome const& outcome, Added const& added);
    void Notify(Notice notice);
    void ShowError(std::string const& key, std::wstring detail = {});
    bool headless_;
    bool exiting_ = false;
    bool ticking_ = false;
    bool adding_ = false;
    bool reopen_ = false;
    bool ending_ = false;
    std::optional<bool> endSaved_;
    bool saving_ = false;
    bool noticeSaving_ = false;
    unsigned sequence_ = 0;
    ULONGLONG waitingSince_ = 0;
    TrayClick trayClick_ = TrayClick::Idle;
    POINT trayPoint_{};
    UINT taskbarCreated_ = RegisterWindowMessageW(L"TaskbarCreated");
    Activity activity_;
    Strings strings_;
    Splash splash_;
    PowerRequest power_;
    Registration registration_;
    Pipe::Client ui_;
    std::deque<Activation> incoming_;
    std::deque<Activation> offered_;
    std::mutex mutex_;
    std::deque<std::function<void()>> requests_;
    // Members are released in reverse order, so the last one goes first: the
    // pipe, whose dispatch uses mutex_ and requests_, then the engine, then the
    // tray and the windows that they use.
    OwnedHandle ownership_;
    OwnedWindow window_;
    OwnedWindow broadcast_;
    std::unique_ptr<Tray> tray_;
    OwnedHandle process_;
    std::unique_ptr<Engine> engine_;
    std::unique_ptr<Pipe> pipe_;
    OwnedNotification powerNotification_;
};
}
