#pragma once
#include "Enums.h"
#include "Strings.h"
#include "Desktop/Popup.h"
#include <windows.h>
#include <oleacc.h>
#include <optional>

namespace tt::desktop
{
// Whether a notice goes to the open window instead of the tray. Problems and
// completions do; additions and the background notice always use the tray, so
// they do not raise the window.
bool UsesWindow(NoticeKind kind);

// The notification-area icon: its tooltip, its menu and its notifications.
class Tray
{
public:
    // `selected` is posted to `window` when the person selects a notice's
    // popup, with the PopupPart as its WPARAM.
    Tray(HWND window, UINT callback, UINT selected, HWND broadcast, Strings const& strings, bool headless);
    ~Tray();
    Tray(Tray const&) = delete;
    Tray& operator=(Tray const&) = delete;
    void Add();
    void Update(Activity activity, bool loading, bool pausable);
    void Translate();
    std::optional<TrayItem> ShowMenu(POINT point);
    BOOL Measure(MEASUREITEMSTRUCT& item);
    BOOL Draw(DRAWITEMSTRUCT const& item);
    void Queue(Notice notice);
    void Notify();
    // The notice whose popup was selected, unless selecting it only dismisses it.
    std::optional<Notice> TakeNotice();
    // The switch that the selected popup's button turns off.
    std::optional<NoticeSwitch> TakeSwitch();
private:
    struct Row
    {
        std::wstring text;
        MSAAMENUINFO name{};
    };
    // The notices that wait to share the next popup.
    struct Batch
    {
        std::optional<Notice> first;
        unsigned count = 0;
        std::optional<Notice> failure;
        unsigned failureCount = 0;
        ULONGLONG due = 0;
    };
    void Apply(DWORD action);
    void Flush();
    void ShowBackground();
    // Adds the button that turns `notices` off, when there is a switch for them.
    void Show(Popup::Content content, std::optional<NoticeSwitch> notices);
    Row* Find(UINT id);
    Row& Fill(TrayItem item);
    void Rename(TrayItem item, std::wstring text);
    int Margin() const;
    std::wstring Rate(std::int64_t bytes) const;
    std::wstring Rates() const;
    std::wstring Counts() const;
    std::wstring Tooltip() const;
    std::wstring PauseText() const;
    Popup::Content Message(Notice const& notice, unsigned count) const;
    Popup::Content Failures(Notice const& first, unsigned count) const;
    std::wstring Problem(std::string const& code) const;
    HWND window_;
    UINT callback_;
    HWND broadcast_;
    Strings const& strings_;
    bool headless_;
    Activity activity_;
    bool loading_ = false;
    bool pausable_ = false;
    HICON errorIcon_ = nullptr;
    std::wstring tooltip_;
    HMENU menu_ = nullptr;
    HFONT font_ = nullptr;
    Row rates_;
    Row counts_;
    Batch batch_;
    std::optional<Notice> notice_;
    std::optional<NoticeSwitch> switch_;
    Popup popup_;
};
}
