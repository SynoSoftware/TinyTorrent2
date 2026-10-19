#include "Desktop/Tray.h"
#include "Resources.h"
#include <algorithm>
#include <shellapi.h>

namespace tt::desktop
{
namespace
{
constexpr UINT iconId = 1;
// Space around a status row's text, in pixels at 96 DPI.
constexpr int rowPadding = 8;

UINT Id(TrayItem item)
{
    return static_cast<UINT>(item);
}
}

Tray::Tray(HWND window, HWND broadcast, Strings const& strings, bool headless, HICON errorIcon, Commands commands)
    : window_(window), broadcast_(broadcast), strings_(strings), headless_(headless),
      commands_(std::move(commands)), errorIcon_(errorIcon)
{
}

std::optional<LRESULT> Tray::Handle(UINT message, WPARAM first, LPARAM second)
{
    // The tray's own address tells its timer apart from the window's others.
    auto timer = reinterpret_cast<UINT_PTR>(this);
    if (message == callback_)
    {
        OnClick(first, second);
        return 0;
    }
    if (message == WM_TIMER && first == timer)
    {
        KillTimer(window_, timer);
        if (click_ == TrayClick::Waiting)
        {
            click_ = TrayClick::Idle;
            Menu(point_);
        }
        return 0;
    }
    if (message == WM_MEASUREITEM)
    {
        return Measure(*reinterpret_cast<MEASUREITEMSTRUCT*>(second));
    }
    if (message == WM_DRAWITEM)
    {
        return Draw(*reinterpret_cast<DRAWITEMSTRUCT const*>(second));
    }
    if (message == taskbarCreated_)
    {
        KillTimer(window_, timer);
        click_ = TrayClick::Idle;
        Add();
        commands_.refresh();
        return 0;
    }
    return std::nullopt;
}

void Tray::OnClick(WPARAM first, LPARAM second)
{
    auto timer = reinterpret_cast<UINT_PTR>(this);
    auto notification = LOWORD(second);
    if (notification == WM_LBUTTONDOWN)
    {
        KillTimer(window_, timer);
        click_ = TrayClick::Idle;
    }
    else if (notification == NIN_SELECT)
    {
        if (click_ == TrayClick::Double)
        {
            click_ = TrayClick::Idle;
            return;
        }
        point_ = {static_cast<short>(LOWORD(first)), static_cast<short>(HIWORD(first))};
        click_ = TrayClick::Waiting;
        // The first release may belong to a double-click; do not open a menu under its second click.
        SetTimer(window_, timer, GetDoubleClickTime(), nullptr);
    }
    else if (notification == WM_LBUTTONDBLCLK)
    {
        KillTimer(window_, timer);
        click_ = TrayClick::Double;
        commands_.open();
    }
    else if (notification == NIN_KEYSELECT)
    {
        KillTimer(window_, timer);
        click_ = TrayClick::Idle;
        commands_.open();
    }
    else if (notification == WM_CONTEXTMENU)
    {
        KillTimer(window_, timer);
        click_ = TrayClick::Idle;
        Menu({static_cast<short>(LOWORD(first)), static_cast<short>(HIWORD(first))});
    }
}

void Tray::Menu(POINT point)
{
    commands_.refresh();
    auto item = ShowMenu(point);
    if (item == TrayItem::Open)
    {
        commands_.open();
    }
    else if (item == TrayItem::Pause)
    {
        commands_.pause();
    }
    else if (item == TrayItem::Exit)
    {
        commands_.exit();
    }
}

Tray::~Tray()
{
    Apply(NIM_DELETE);
}

void Tray::Add()
{
    Apply(NIM_ADD);
}

void Tray::Remove()
{
    Apply(NIM_DELETE);
}

void Tray::Apply(DWORD action)
{
    if (headless_)
    {
        return;
    }
    NOTIFYICONDATAW icon{sizeof(icon)};
    icon.hWnd = window_;
    icon.uID = iconId;
    icon.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP | NIF_SHOWTIP;
    icon.uCallbackMessage = callback_;
    icon.hIcon = ShowsError() && errorIcon_ ? errorIcon_ :
        LoadIconW(GetModuleHandleW(nullptr), MAKEINTRESOURCEW(IDI_TINYTORRENT));
    tooltip_ = Tooltip();
    wcsncpy_s(icon.szTip, tooltip_.c_str(), _TRUNCATE);
    if (Shell_NotifyIconW(action, &icon) && action == NIM_ADD)
    {
        icon.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, &icon);
    }
}

void Tray::Update(Activity activity, bool loading, bool pausable)
{
    bool hadError = ShowsError();
    activity_ = std::move(activity);
    loading_ = loading;
    pausable_ = pausable;
    if (menu_)
    {
        Rename(TrayItem::Rates, Fill(TrayItem::Rates).text);
        Rename(TrayItem::Counts, Fill(TrayItem::Counts).text);
        Rename(TrayItem::Pause, PauseText());
        EnableMenuItem(menu_, Id(TrayItem::Pause), MF_BYCOMMAND | (pausable_ ? MF_ENABLED : MF_GRAYED));
    }
    if (Tooltip() != tooltip_ || hadError != ShowsError())
    {
        Apply(NIM_MODIFY);
    }
}

void Tray::Translate()
{
    if (menu_)
    {
        EndMenu();
    }
    Apply(NIM_MODIFY);
}

std::optional<TrayItem> Tray::ShowMenu(POINT point)
{
    SetWindowPos(broadcast_, nullptr, point.x, point.y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    NONCLIENTMETRICSW metrics{sizeof(metrics)};
    SystemParametersInfoForDpi(SPI_GETNONCLIENTMETRICS, sizeof(metrics), &metrics, 0,
        GetDpiForWindow(broadcast_));
    font_ = CreateFontIndirectW(&metrics.lfMenuFont);
    SetForegroundWindow(broadcast_);
    UINT selected;
    // Translate ends the menu, and it opens again in the new language.
    for (;;)
    {
        menu_ = CreatePopupMenu();
        auto language = strings_.Language();
        for (auto item : {TrayItem::Rates, TrayItem::Counts})
        {
            auto& row = Fill(item);
            AppendMenuW(menu_, MF_STRING | MF_DISABLED, Id(item), row.text.c_str());
            MENUITEMINFOW info{sizeof(info)};
            info.fMask = MIIM_FTYPE | MIIM_DATA;
            info.fType = MFT_OWNERDRAW;
            info.dwItemData = reinterpret_cast<ULONG_PTR>(&row.name);
            SetMenuItemInfoW(menu_, Id(item), FALSE, &info);
        }
        AppendMenuW(menu_, MF_SEPARATOR, 0, nullptr);
        AppendMenuW(menu_, MF_STRING, Id(TrayItem::Open), strings_.Text("tray", "open").c_str());
        AppendMenuW(menu_, MF_STRING, Id(TrayItem::Pause), PauseText().c_str());
        EnableMenuItem(menu_, Id(TrayItem::Pause), MF_BYCOMMAND | (pausable_ ? MF_ENABLED : MF_GRAYED));
        AppendMenuW(menu_, MF_SEPARATOR, 0, nullptr);
        AppendMenuW(menu_, MF_STRING, Id(TrayItem::Exit), strings_.Text("tray", "exit").c_str());
        SetMenuDefaultItem(menu_, Id(TrayItem::Open), FALSE);
        selected = TrackPopupMenu(menu_, TPM_RETURNCMD | TPM_RIGHTBUTTON, point.x, point.y, 0, broadcast_,
            nullptr);
        DestroyMenu(menu_);
        menu_ = nullptr;
        if (language == strings_.Language())
        {
            break;
        }
    }
    DeleteObject(font_);
    font_ = nullptr;
    PostMessageW(broadcast_, WM_NULL, 0, 0);
    if (!selected)
    {
        return std::nullopt;
    }
    return static_cast<TrayItem>(selected);
}

Tray::Row* Tray::Find(UINT id)
{
    if (id == Id(TrayItem::Rates))
    {
        return &rates_;
    }
    if (id == Id(TrayItem::Counts))
    {
        return &counts_;
    }
    return nullptr;
}

// Status rows are disabled items drawn by the tray, so they read as text
// rather than as unavailable commands. Screen readers read their names.
Tray::Row& Tray::Fill(TrayItem item)
{
    auto& row = *Find(Id(item));
    row.text = item == TrayItem::Rates ? Rates() : Counts();
    row.name = {MSAA_MENU_SIG, static_cast<DWORD>(row.text.size()), row.text.data()};
    return row;
}

void Tray::Rename(TrayItem item, std::wstring text)
{
    MENUITEMINFOW info{sizeof(info)};
    info.fMask = MIIM_STRING;
    info.dwTypeData = text.data();
    SetMenuItemInfoW(menu_, Id(item), FALSE, &info);
}

int Tray::Margin() const
{
    auto dpi = GetDpiForWindow(broadcast_);
    return GetSystemMetricsForDpi(SM_CXMENUCHECK, dpi) + MulDiv(rowPadding, dpi, USER_DEFAULT_SCREEN_DPI);
}

BOOL Tray::Measure(MEASUREITEMSTRUCT& item)
{
    auto row = Find(item.itemID);
    if (!row)
    {
        return FALSE;
    }
    auto dpi = GetDpiForWindow(broadcast_);
    auto dc = GetDC(broadcast_);
    auto previous = SelectObject(dc, font_);
    SIZE size{};
    GetTextExtentPoint32W(dc, row->text.c_str(), static_cast<int>(row->text.size()), &size);
    item.itemWidth = size.cx + Margin() * 2;
    item.itemHeight = std::max<LONG>(size.cy + MulDiv(rowPadding, dpi, USER_DEFAULT_SCREEN_DPI),
        GetSystemMetricsForDpi(SM_CYMENU, dpi));
    SelectObject(dc, previous);
    ReleaseDC(broadcast_, dc);
    return TRUE;
}

BOOL Tray::Draw(DRAWITEMSTRUCT const& item)
{
    auto row = Find(item.itemID);
    if (!row)
    {
        return FALSE;
    }
    FillRect(item.hDC, &item.rcItem, GetSysColorBrush(COLOR_MENU));
    auto previous = SelectObject(item.hDC, font_);
    auto color = SetTextColor(item.hDC, GetSysColor(COLOR_MENUTEXT));
    auto background = SetBkMode(item.hDC, TRANSPARENT);
    auto bounds = item.rcItem;
    bounds.left += Margin();
    DrawTextW(item.hDC, row->text.c_str(), static_cast<int>(row->text.size()), &bounds,
        DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX);
    SetBkMode(item.hDC, background);
    SetTextColor(item.hDC, color);
    SelectObject(item.hDC, previous);
    return TRUE;
}

std::wstring Tray::Rate(std::int64_t bytes) const
{
    char const* units[] = {"bytes", "kib", "mib", "gib"};
    double value = static_cast<double>(bytes);
    std::size_t unit = 0;
    while (value >= 1024 && unit + 1 < std::size(units))
    {
        value /= 1024;
        ++unit;
    }
    return strings_.Format("units", units[unit], {Strings::Number(value, unit ? 1U : 0U)});
}

std::wstring Tray::Rates() const
{
    auto download = Rate(activity_.downloadRate);
    auto upload = Rate(activity_.uploadRate);
    return strings_.Format("tray", "rates", {download, upload});
}

std::wstring Tray::Counts() const
{
    auto count = activity_.torrentCount;
    if (activity_.paused)
    {
        return strings_.Format("tray", count == 1 ? "paused_one" : "paused",
            {Strings::Number(static_cast<double>(count))});
    }
    auto active = Strings::Number(activity_.activeCount);
    auto queued = Strings::Number(activity_.queuedCount);
    return strings_.Format("tray", "counts", {active, queued});
}

bool Tray::ShowsError() const
{
    return activity_.toasts.contains(ToastSwitch::Problems) && activity_.errorCount;
}

std::wstring Tray::Tooltip() const
{
    auto errors = activity_.errorCount ?
        strings_.Format("tray", "errors", {Strings::Number(activity_.errorCount)}) + L"\n" : std::wstring();
    if (loading_)
    {
        return errors + strings_.Text("startup", "loading");
    }
    if (activity_.paused)
    {
        if (activity_.missingAdapter)
        {
            return errors + strings_.Format("tray", "missing_adapter", {Wide(*activity_.missingAdapter)});
        }
        return errors + Counts();
    }
    return errors + Rates() + L"\n" + Counts();
}

std::wstring Tray::PauseText() const
{
    return strings_.Text("tray", activity_.pausedByChoice ? "resume" : "pause");
}
}
