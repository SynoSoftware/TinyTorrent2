#pragma once
#include "Enums.h"
#include "Strings.h"
#include "Desktop/Popup.h"
#include <filesystem>
#include <functional>
#include <map>
#include <optional>

namespace tt::desktop
{
// Whether a notice goes to the open window instead of a popup. Problems and
// completions do; additions and the background notice always use a popup, so
// they do not raise the window.
bool UsesWindow(NoticeKind kind);

// The popups above the notification area: which notices and failures show,
// which share a popup, and what selecting each part of a popup does.
class Notices
{
public:
    // What popups do that the desktop host owns.
    struct Commands
    {
        std::function<void()> open;
        // Opens the window at a card on the General page of Settings.
        std::function<void(std::string card)> openSettings;
        // The folder that holds a torrent's files; empty when it has none.
        std::function<std::string(std::string const& torrentId)> folder;
        std::function<void(std::vector<NoticeSwitch> notices)> turnOff;
    };

    // Something the person asked the desktop host to do that failed, such as
    // the final save on Exit. It always shows, alone, until the person closes
    // it, because staying silent would leave the person believing it worked.
    struct Failure
    {
        // Names its message in the error group, and tells failures apart.
        std::string code;
        std::wstring detail;
        // The buttons that resolve it, made by Retry, OpenFolder or ForceExit.
        std::vector<Popup::Action> actions;
    };

    // The caller keeps `errorIcon`, which marks problems and failures, alive.
    Notices(Strings const& strings, bool headless, HICON errorIcon, Commands commands);
    Notices(Notices const&) = delete;
    Notices& operator=(Notices const&) = delete;
    // Takes the notification switches and the theme.
    void Update(Activity const& activity);
    void Queue(Notice notice);
    // A failure with the code of one that waits replaces it, and one that is
    // on screen is not shown again.
    void Report(Failure failure);
    // Shows the first failure, or else the batch of notices that is due first,
    // once the shown popup has closed.
    void Notify();
    void Close();
    // The buttons that failures offer, so each looks and reads the same
    // wherever it shows. Retry opens the window again.
    Popup::Action Retry() const;
    Popup::Action OpenFolder(std::filesystem::path folder, std::wstring tooltip) const;
    Popup::Action ForceExit(std::function<void()> run) const;
#ifdef _DEBUG
    // Shows an Added notice for a sample torrent, whatever the switches say.
    void ShowSample();
#endif
private:
    // The notices that wait to share the next popup.
    struct Batch
    {
        Notice first;
        unsigned count;
        ULONGLONG due;
    };
    bool Accepts(NoticeKind kind) const;
    bool IsOn(NoticeSwitch notices) const;
    void ShowBackground();
    void ShowFailure(Failure failure);
    // Offers the More menu when there is a switch that turns `notices` off.
    void Show(Popup::Content content, std::optional<NoticeSwitch> notices);
    Popup::Content Message(Notice const& notice) const;
    Popup::Content Problems(Notice const& first, unsigned count) const;
    std::wstring ErrorText(std::string const& code) const;
    // What selecting the message of a popup that shows `notice` does.
    std::function<void()> Selection(Notice const& notice) const;
    Strings const& strings_;
    bool headless_;
    HICON errorIcon_;
    Commands commands_;
    Activity activity_;
    std::map<NoticeKind, Batch> batches_;
    // In the order they were reported, one per code.
    std::vector<Failure> failures_;
    // The code of the failure that the open popup shows; none while it shows
    // a notice.
    std::optional<std::string> shownFailure_;
    Popup popup_;
};
}
