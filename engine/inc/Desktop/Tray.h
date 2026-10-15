#pragma once
#include "Enums.h"
#include "Strings.h"
#include <windows.h>
#include <oleacc.h>
#include <optional>

namespace tt::desktop
{
// The notification-area icon: its tooltip and its menu.
class Tray
{
public:
    // The icon shows `errorIcon`, which the caller keeps alive, while
    // transfers have problems.
    Tray(HWND window, UINT callback, HWND broadcast, Strings const& strings, bool headless, HICON errorIcon);
    ~Tray();
    Tray(Tray const&) = delete;
    Tray& operator=(Tray const&) = delete;
    void Add();
    // Takes the icon off the screen before the process ends.
    void Remove();
    void Update(Activity activity, bool loading, bool pausable);
    void Translate();
    std::optional<TrayItem> ShowMenu(POINT point);
    BOOL Measure(MEASUREITEMSTRUCT& item);
    BOOL Draw(DRAWITEMSTRUCT const& item);
private:
    struct Row
    {
        std::wstring text;
        MSAAMENUINFO name{};
    };
    void Apply(DWORD action);
    Row* Find(UINT id);
    Row& Fill(TrayItem item);
    void Rename(TrayItem item, std::wstring text);
    int Margin() const;
    std::wstring Rate(std::int64_t bytes) const;
    std::wstring Rates() const;
    std::wstring Counts() const;
    std::wstring Tooltip() const;
    std::wstring PauseText() const;
    HWND window_;
    UINT callback_;
    HWND broadcast_;
    Strings const& strings_;
    bool headless_;
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
