#include "Desktop/Tray.h"
#include "Resources.h"
#include <algorithm>
#include <filesystem>
#include <shellapi.h>

namespace tt::desktop
{
namespace
{
constexpr UINT iconId = 1;
// Notices that arrive within this many milliseconds share one balloon.
constexpr ULONGLONG noticeDelay = 1000;
// A balloon holds 255 characters, so a failure summary shortens the name and
// the detail.
constexpr std::size_t nameLength = 60;
constexpr std::size_t detailLength = 120;
// Space around a status row's text, in pixels at 96 DPI.
constexpr int rowPadding = 8;

UINT Id(TrayItem item)
{
    return static_cast<UINT>(item);
}
}

Tray::Tray(HWND window, UINT callback, HWND broadcast, Strings const& strings, bool headless)
    : window_(window), callback_(callback), broadcast_(broadcast), strings_(strings), headless_(headless)
{
}

Tray::~Tray()
{
    Apply(NIM_DELETE);
}

void Tray::Add()
{
    Apply(NIM_ADD);
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
    icon.hIcon = LoadIconW(GetModuleHandleW(nullptr), MAKEINTRESOURCEW(IDI_TINYTORRENT));
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
    if (Tooltip() != tooltip_)
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
    wchar_t number[64];
    swprintf_s(number, unit ? L"%.1f" : L"%.0f", value);
    wchar_t decimal[8];
    wchar_t const* locale = LOCALE_NAME_USER_DEFAULT;
    GetLocaleInfoEx(locale, LOCALE_SDECIMAL, decimal, static_cast<int>(std::size(decimal)));
    NUMBERFMTW format{.NumDigits = unit ? 1U : 0U, .LeadingZero = 1, .Grouping = 0,
        .lpDecimalSep = decimal, .lpThousandSep = const_cast<LPWSTR>(L""), .NegativeOrder = 1};
    wchar_t formatted[64];
    if (GetNumberFormatEx(locale, 0, number, &format, formatted, static_cast<int>(std::size(formatted))))
    {
        wcscpy_s(number, formatted);
    }
    return strings_.Format("units", units[unit], {number});
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
    if (activity_.allPaused)
    {
        return strings_.Format("tray", count == 1 ? "paused_one" : "paused", {std::to_wstring(count)});
    }
    auto active = std::to_wstring(activity_.active);
    auto queued = std::to_wstring(activity_.queued);
    return strings_.Format("tray", "counts", {active, queued});
}

std::wstring Tray::Tooltip() const
{
    if (loading_)
    {
        return strings_.Text("startup", "loading");
    }
    if (activity_.allPaused)
    {
        if (!activity_.missingInterface.empty())
        {
            return strings_.Format("tray", "missing_interface", {Wide(activity_.missingInterface)});
        }
        return Counts();
    }
    return Rates() + L"\n" + Counts();
}

std::wstring Tray::PauseText() const
{
    return strings_.Text("tray", activity_.allPaused ? "resume" : "pause");
}

// The notifications preference covers completions only: nothing else confirms
// a direct addition while the window is closed.
void Tray::Queue(Notice notice, bool windowShows)
{
    auto kind = notice.kind;
    bool failure = kind == NoticeKind::Error || kind == NoticeKind::AddFailed || kind == NoticeKind::DeleteFailed;
    if (headless_ || (failure && windowShows && kind != NoticeKind::DeleteFailed) ||
        (kind == NoticeKind::Completed && !activity_.notificationsEnabled))
    {
        return;
    }
    if (failure)
    {
        if (!batch_.failure)
        {
            batch_.failure = notice;
        }
        ++batch_.failureCount;
    }
    if (!batch_.first)
    {
        batch_.first = std::move(notice);
    }
    ++batch_.count;
    if (!batch_.due)
    {
        batch_.due = GetTickCount64() + noticeDelay;
    }
}

// Shows the notices that are due as one balloon. A single notice shows its own
// message; several show a count, and any failure among them shows a summary.
void Tray::Notify()
{
    if (!batch_.due || GetTickCount64() < batch_.due)
    {
        return;
    }
    auto batch = std::exchange(batch_, {});
    notification_ = batch.count == 1 ? std::move(*batch.first) : Notice{NoticeKind::Aggregate};
    QUERY_USER_NOTIFICATION_STATE state{};
    bool accepted = SUCCEEDED(SHQueryUserNotificationState(&state)) && state == QUNS_ACCEPTS_NOTIFICATIONS;
    if (!accepted)
    {
        notification_.reset();
    }
    else if (batch.failure)
    {
        Balloon(Failures(*batch.failure, batch.failureCount), true);
    }
    else
    {
        Balloon(Message(*notification_, batch.count), false);
    }
}

std::wstring Tray::Message(Notice const& notice, unsigned count) const
{
    std::wstring message;
    if (notice.kind == NoticeKind::Aggregate)
    {
        message = strings_.Format("notification", "aggregate", {std::to_wstring(count)});
    }
    else if (notice.kind == NoticeKind::Background)
    {
        message = strings_.Text("tray", "background");
    }
    else
    {
        message = strings_.Format("notification", ToString(notice.kind), {Wide(notice.name)});
    }
    if (!notice.detail.empty())
    {
        message += L"\n" + Wide(notice.detail);
    }
    return message;
}

std::wstring Tray::Failures(Notice const& first, unsigned count) const
{
    auto name = Wide(first.name);
    if (first.kind == NoticeKind::AddFailed)
    {
        name = IsMagnet(first.name) ? strings_.Text("notification", "magnet") :
            std::filesystem::path(name).filename().wstring();
    }
    auto detail = Wide(first.detail);
    return strings_.Format("notification", "failures",
        {std::to_wstring(count), name.substr(0, nameLength), detail.substr(0, detailLength)});
}

void Tray::Balloon(std::wstring const& message, bool error)
{
    NOTIFYICONDATAW icon{sizeof(icon)};
    icon.hWnd = window_;
    icon.uID = iconId;
    icon.uFlags = NIF_INFO | NIF_REALTIME;
    icon.dwInfoFlags = (error ? NIIF_ERROR : NIIF_INFO) | NIIF_RESPECT_QUIET_TIME;
    wcscpy_s(icon.szInfoTitle, productName);
    wcsncpy_s(icon.szInfo, message.c_str(), _TRUNCATE);
    Shell_NotifyIconW(NIM_MODIFY, &icon);
}

std::optional<Notice> Tray::TakeNotification()
{
    return std::exchange(notification_, std::nullopt);
}
}
