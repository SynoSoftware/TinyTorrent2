#pragma once
#include "Enums.h"
#include "Strings.h"
#include "Desktop/Popup.h"
#include <filesystem>
#include <functional>
#include <map>
#include <memory>
#include <optional>

namespace tt::desktop
{
// Whether a toast goes to the open window instead of a popup. Problems and
// completions do; additions and the background toast always use a popup, so
// they do not raise the window.
bool UsesWindow(ToastKind kind);
// The Settings switch that turns off toasts of `kind`, wherever they show.
std::optional<Setting> Switch(ToastKind kind);
// The product icon with Windows' error mark, which the tray and the popups
// show for problems. The caller destroys it.
HICON ErrorIcon();

// The popups above the notification area: which toasts and failures show,
// which share a popup, and what selecting each part of a popup does.
class Toasts
{
public:
    // What popups do that the desktop host owns.
    struct Commands
    {
        std::function<void()> open;
        std::function<void(std::string torrentId)> openTorrent;
        // Continues every torrent that waits for the person's Retry.
        std::function<void()> retryAll;
        // Opens the window at a card on the General page of Settings.
        std::function<void(std::string card)> openSettings;
        // The folder that holds a torrent's files; empty when it has none.
        std::function<std::string(std::string const& torrentId)> folder;
        // Shows a toast in the product window; false when none is connected.
        std::function<bool(Toast const& toast)> window;
    };

    // Something the person asked the desktop host to do that failed, such as
    // the final save on Exit. It always shows, alone, until the person closes
    // it, because staying silent would leave the person believing it worked.
    struct Failure
    {
        // Names its message in the error group, and tells failures apart.
        std::string code;
        // Names the failure; without it, the title only says that TinyTorrent
        // needs attention.
        std::optional<std::wstring> title;
        std::wstring detail;
        // The buttons that resolve it, made by Retry, OpenFolder or ForceExit.
        std::vector<Popup::Action> actions;
        // It answers the person's own action, such as a launch, Exit or
        // adding a torrent, so it takes the focus when it shows at once. A
        // failure that arrives in the background, or waits behind another
        // popup, never takes it, because the person may be typing elsewhere.
        bool interactive = false;
        // Selecting its message opens the window. A failure shown while the
        // window cannot open, such as during the final save, leaves this off,
        // so selecting its message does nothing.
        bool opens = true;
        // Startup failures use the same content before an engine exists.
        Popup::Content Content(Strings const& strings, HICON errorIcon) const;
    };

    // The caller keeps `errorIcon`, which marks problems and failures, alive.
    Toasts(Engine& engine, Strings const& strings, bool headless, HICON errorIcon, Commands commands);
    Toasts(Toasts const&) = delete;
    Toasts& operator=(Toasts const&) = delete;
    // Takes the notification switches and the theme.
    void Update(Activity const& activity);
    // Whether the switch that governs `kind` is on, wherever the toast shows.
    // A kind without a switch is always on.
    bool IsOn(ToastKind kind) const;
    // Shows a toast whose switch is on in the window when it uses one, and
    // otherwise in a popup. A headless engine writes it to the debug output.
    void Queue(Toast toast);
    // A failure with the code of one that waits replaces it, and one that is
    // on screen is not shown again. A headless engine writes it to the debug
    // output.
    void Report(Failure failure);
    // Shows the first failure, or else the batch of toasts that is due first,
    // once the shown popup has closed.
    void Notify();
    void Close();
    // Shows a failure at once, even while Windows holds notifications back,
    // because it answers something the person just did.
    void ShowFailure(Failure failure);
    // Replaces the shown popup with a prompt. A failure it shows and later
    // toasts wait behind the prompt.
    // An off setting gives the enabled answer without showing a prompt.
    // Its opt-out menu saves the setting before giving the answer.
    void Ask(Popup::Prompt prompt, std::optional<Setting> setting = {});
    bool IsOpen() const { return popup_.IsOpen() || (answering_ && answering_->pending); }
    // The buttons that failures and prompts offer, so each looks and reads the
    // same wherever it shows.
    static Popup::Action Retry(Strings const& strings, std::wstring tooltip, std::function<void()> run);
    static Popup::Action OpenFolder(Strings const& strings, std::filesystem::path folder, std::wstring tooltip);
    static Popup::Action ForceExit(Strings const& strings, std::function<void()> run);
#ifdef _DEBUG
    // Shows an Added toast for a sample torrent, whatever the switches say.
    void ShowSample();
#endif
private:
    struct Answer
    {
        Popup::Action action;
        bool pending = true;
    };
    // The toasts that wait to share the next popup.
    struct Batch
    {
        Toast first;
        unsigned count;
        ULONGLONG due;
    };
    void ShowBackground();
    void Show(Popup::Content content, std::optional<Setting> setting);
    void SetMenu(Popup::Content& content, std::optional<Setting> setting);
    Popup::MenuItem OptOut(Setting setting, std::wstring label, std::shared_ptr<Answer> answer = {});
    void TurnOff(std::vector<Setting> switches, std::function<void(bool saved)> completion = {});
    Popup::Content Message(Toast const& toast) const;
    Popup::Content Problems(Toast const& first, unsigned count) const;
    // The code's sentence. A code without one gets the generic sentence,
    // unless `explained`, because a detail follows that says what failed.
    static std::wstring ErrorText(Strings const& strings, std::string const& code, bool explained = false);
    // What selecting the message of a popup that shows `toast` does.
    std::function<void()> Selection(Toast const& toast) const;
    Engine& engine_;
    Strings const& strings_;
    bool headless_;
    HICON errorIcon_;
    Commands commands_;
    Activity activity_;
    std::map<std::pair<ToastKind, std::string>, Batch> batches_;
    // In the order they were reported, one per code.
    std::vector<Failure> failures_;
    // The failure that the open popup shows; none while it shows a toast.
    std::optional<Failure> shownFailure_;
    std::optional<std::string> shownCondition_;
    // Keeps other toasts behind the prompt until its caller consumes the answer.
    std::shared_ptr<Answer> answering_;
    Popup popup_;
};
}
