#pragma once
#include "Enums.h"
#include "Strings.h"
#include <windows.h>
#include <oleacc.h>
#include <functional>
#include <optional>

namespace tt::desktop
{
// The notification-area icon: its tooltip, its menu and what selecting it
// does.
class Tray
{
public:
    // What the icon and its menu run, which the desktop host owns.
    struct Commands
    {
        std::function<void()> open;
        std::function<void()> pause;
        std::function<void()> exit;
        // Brings the rates and counts up to date before the menu opens.
        std::function<void()> refresh;
    };

    // The icon belongs to `window`, which passes its messages to Handle, and
    // the menu to `broadcast`, which forwards the menu's drawing messages to
    // `window`. It shows `errorIcon`, which the caller keeps alive, while
    // transfers have problems.
    Tray(HWND window, HWND broadcast, Strings const& strings, bool headless, HICON errorIcon, Commands commands);
    ~Tray();
    Tray(Tray const&) = delete;
    Tray& operator=(Tray const&) = delete;
    void Add();
    // Takes the icon off the screen before the process ends.
    void Remove();
    void Update(Activity activity, bool loading, bool pausable);
    void Translate();
    // Answers the icon's own messages; none for any other message.
    std::optional<LRESULT> Handle(UINT message, WPARAM first, LPARAM second);
private:
    struct Row
    {
        std::wstring text;
        MSAAMENUINFO name{};
    };
    void Apply(DWORD action);
    void OnClick(WPARAM first, LPARAM second);
    void Menu(POINT point);
    std::optional<TrayItem> ShowMenu(POINT point);
    BOOL Measure(MEASUREITEMSTRUCT& item);
    BOOL Draw(DRAWITEMSTRUCT const& item);
    Row* Find(UINT id);
    Row& Fill(TrayItem item);
    void Rename(TrayItem item, std::wstring text);
    int Margin() const;
    std::wstring Rate(std::int64_t bytes) const;
    std::wstring Rates() const;
    std::wstring Counts() const;
    std::wstring Tooltip() const;
    // The icon shows errors only while problem notifications are on.
    bool ShowsError() const;
    std::wstring PauseText() const;
    HWND window_;
    HWND broadcast_;
    Strings const& strings_;
    bool headless_;
    Commands commands_;
    UINT callback_ = RegisterWindowMessageW(L"TinyTorrent.Tray");
    // Explorer sends it when it starts again, which removes every icon.
    UINT taskbarCreated_ = RegisterWindowMessageW(L"TaskbarCreated");
    TrayClick click_ = TrayClick::Idle;
    // Where the single click that waits for the double-click time happened.
    POINT point_{};
    Activity activity_;
    bool loading_ = false;
    bool pausable_ = false;
    HICON errorIcon_;
    std::wstring tooltip_;
    HMENU menu_ = nullptr;
    HFONT font_ = nullptr;
    Row rates_;
    Row counts_;
};
}
