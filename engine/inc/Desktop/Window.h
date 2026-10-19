#pragma once
#include "Owned.h"
#include "Pipe.h"
#include "Strings.h"
#include "Desktop/Notices.h"
#include "Desktop/Splash.h"
#include <deque>
#include <optional>

namespace tt::desktop
{
// The most sources that wait to be added at one time.
inline constexpr std::size_t sourceLimit = 256;

// Whether a launch or an activation may hand over `sources`.
bool ValidSources(std::vector<std::string> const& sources);

// Sources that a later launch or the shell handed to this engine.
struct Activation
{
    std::string activationId;
    bool batch;
    std::vector<std::string> sources;
};

// The product window: its process, its connection to this engine, what the
// engine waits for from it, and the splash that covers its start.
class Window
{
public:
    // What the window's state decides for Exit, which the desktop host owns.
    struct Commands
    {
        // The window has gone while Exit closed it, or a headless engine's
        // window stopped answering.
        std::function<void()> shutdown;
        // The window cancelled Exit, vanished while it asked, or stopped
        // answering.
        std::function<void()> cancelExit;
        // The window's answer to `confirm_exit`.
        std::function<void(ExitAnswer answer)> answer;
    };

    // The caller keeps `strings`, `engine` and `notices` alive.
    Window(Strings const& strings, Engine& engine, Notices& notices, bool headless, Commands commands);
    Window(Window const&) = delete;
    Window& operator=(Window const&) = delete;

    // Starts or raises the window; false while it closes for Exit or the
    // engine shuts down.
    bool Open();
    // Opens the window at `card`, a card on the General page of Settings,
    // which the window highlights.
    void OpenSettings(std::string card);
    // Opens the window the person asked for once its old process has ended.
    void Reopen();
    // Asks the connected window to confirm Exit; false without one.
    bool Confirm();
    // Asks the window to end for Exit and waits for it; false when no window
    // runs.
    bool Close();
    // Windows ends the session and the window with it, so nothing more is
    // waited for and a window that closes is not reported. Only a window
    // whose `ready` reply the splash still holds is sent `close`.
    void End();
    // Hands sources to the window's Add dialog, opening it when needed.
    void Offer(Activation activation);
    // How many sources wait for the window's Add dialog.
    std::size_t Offered() const;
    // Shows a notice in the connected window; false without one.
    bool Notify(Notice const& notice);
    bool Owns(std::shared_ptr<Pipe::Connection> const& client) const { return client == client_; }
    void OnReady(std::shared_ptr<Pipe::Connection> const& client, Reply const& reply);
    void OnClosed(Reply const& reply);
    void OnActivateReply(std::shared_ptr<Pipe::Connection> const& client, bool available, Reply const& reply);
    void OnCloseReply(std::shared_ptr<Pipe::Connection> const& client, CloseState state, Reply const& reply);
    void OnExitReply(std::shared_ptr<Pipe::Connection> const& client, ExitAnswer answer, Reply const& reply);
    void OnPendingActivations(std::shared_ptr<Pipe::Connection> const& client, Reply const& reply);
    void OnActivationsReceived(std::shared_ptr<Pipe::Connection> const& client,
        std::vector<std::string> const& ids, Reply const& reply);
    void Disconnect(std::shared_ptr<Pipe::Connection> const& client);
    // Ends a wait whose window has gone, appeared on its own or timed out.
    void OnTimer();
    // Takes the splash theme, and shows the splash once the saved choice to
    // show it is known.
    void Refresh(Theme theme);
    void Translate();
private:
    struct Wait
    {
        WindowWait kind;
        // When it began; none while the window waits for the person, such as
        // over its own prompt.
        std::optional<ULONGLONG> since;
    };
    bool IsClosing() const;
    bool IsRunning() const;
    // The launched window when it is on screen without a connection to this
    // engine, showing its own failure, such as an engine of another version.
    HWND Shown() const;
    void Forget();
    void ShowSplash();
    void ShowSettings();
    void ShowStartError(Notices::Failure failure);
    Strings const& strings_;
    Engine& engine_;
    Notices& notices_;
    bool headless_;
    Commands commands_;
    std::optional<Wait> wait_;
    // The person asked for the window while an old window process could not
    // show it, so a new one opens once that process has ended.
    bool reopen_ = false;
    // The Settings card to show once the window connects.
    std::optional<std::string> settingsCard_;
    // Cold when the current wait for a window began while the engine loaded.
    Launch launch_ = Launch::Warm;
    // When the warm launch now waiting for `ready` started its window process.
    std::optional<ULONGLONG> launchedAt_;
    std::deque<Activation> offered_;
    std::shared_ptr<Pipe::Connection> client_;
    OwnedHandle process_;
    Splash splash_;
};
}
