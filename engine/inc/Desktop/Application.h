#pragma once
#include "Pipe.h"
#include "Registration.h"
#include "Strings.h"
#include "Desktop/PowerRequest.h"
#include "Desktop/Splash.h"
#include "Desktop/Tray.h"
#include <optional>

namespace tiny::desktop
{
class Application
{
public:
    Application(std::filesystem::path directory, std::wstring sid, bool headless);
    ~Application();
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
    void BeginShutdown();
    void EndSession();
    void Tick();
    void Refresh();
    void Pause();
    void ShowSplash(std::optional<SplashFailure> failure = {}, std::wstring detail = {});
    void Resolve(SplashFailure failure, SplashChoice choice);
    Json Activate(std::vector<std::string> sources);
    void AddSources();
    void FinishSource(Json const& response);
    void Notify(Notice notice);
    void ShowError(std::string const& key, std::wstring detail = {});
    HWND window_ = nullptr;
    HWND broadcast_ = nullptr;
    HANDLE ownership_ = INVALID_HANDLE_VALUE;
    HANDLE process_ = nullptr;
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
    UINT taskbarCreated_ = RegisterWindowMessageW(L"TaskbarCreated");
    HPOWERNOTIFY powerNotification_ = nullptr;
    Activity activity_;
    Strings strings_;
    Splash splash_;
    PowerRequest power_;
    std::unique_ptr<Tray> tray_;
    std::unique_ptr<Engine> engine_;
    std::unique_ptr<Pipe> pipe_;
    Registration registration_;
    Pipe::Client ui_;
    std::deque<Activation> waiting_;
    std::deque<Activation> activations_;
    std::mutex mutex_;
    std::deque<std::function<void()>> pending_;
};
}
