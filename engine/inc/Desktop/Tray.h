#pragma once
#include "Enums.h"
#include "Strings.h"
#include <windows.h>
#include <oleacc.h>
#include <optional>
#include <vector>

namespace tt::desktop
{
// The notification-area icon: its tooltip, its menu and its balloon
// notifications.
class Tray
{
public:
    Tray(HWND window, UINT callback, HWND broadcast, Strings const& strings, bool headless);
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
    std::vector<Notice> Notify(bool windowShows);
    std::optional<Notice> TakeNotification();
private:
    struct Row
    {
        std::wstring text;
        MSAAMENUINFO name{};
    };
    // The notices that wait to share the next balloon.
    struct Batch
    {
        std::optional<Notice> first;
        unsigned count = 0;
        std::optional<Notice> failure;
        unsigned failureCount = 0;
        std::optional<Notice> completion;
        unsigned completionCount = 0;
        bool added = false;
        ULONGLONG due = 0;
    };
    void Apply(DWORD action);
    void Balloon(std::wstring const& message, bool error);
    Row* Find(UINT id);
    Row& Fill(TrayItem item);
    void Rename(TrayItem item, std::wstring text);
    int Margin() const;
    std::wstring Rate(std::int64_t bytes) const;
    std::wstring Rates() const;
    std::wstring Counts() const;
    std::wstring Tooltip() const;
    std::wstring PauseText() const;
    std::wstring Message(Notice const& notice, unsigned count) const;
    std::wstring Failures(Notice const& first, unsigned count) const;
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
    std::optional<Notice> notification_;
};
}
