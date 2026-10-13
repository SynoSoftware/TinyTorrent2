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
// Notices that arrive within this many milliseconds share one popup.
constexpr ULONGLONG noticeDelay = 1000;
// Space around a status row's text, in pixels at 96 DPI.
constexpr int rowPadding = 8;

UINT Id(TrayItem item)
{
    return static_cast<UINT>(item);
}

HICON ErrorIcon()
{
    ICONINFO icon{};
    if (!GetIconInfo(LoadIconW(GetModuleHandleW(nullptr), MAKEINTRESOURCEW(IDI_TINYTORRENT)), &icon))
    {
        return nullptr;
    }
    HICON result = nullptr;
    auto target = CreateCompatibleDC(nullptr);
    BITMAP color{};
    if (target && icon.hbmColor && GetObjectW(icon.hbmColor, static_cast<int>(sizeof(color)), &color))
    {
        auto previous = SelectObject(target, icon.hbmColor);
        auto width = static_cast<int>(color.bmWidth / 2);
        auto height = static_cast<int>(color.bmHeight / 2);
        auto drawn = DrawIconEx(target, width, height, LoadIconW(nullptr, IDI_ERROR), width, height, 0, nullptr, DI_NORMAL);
        SelectObject(target, previous);
        if (drawn)
        {
            result = CreateIconIndirect(&icon);
        }
    }
    if (target) DeleteDC(target);
    DeleteObject(icon.hbmColor);
    DeleteObject(icon.hbmMask);
    return result;
}

// The switch that turns off notices of `kind`. A failure of something the
// person just asked for has none, because it always shows.
std::optional<NoticeSwitch> Switch(NoticeKind kind)
{
    switch (kind)
    {
    case NoticeKind::Completed:
        return NoticeSwitch::Finished;
    case NoticeKind::Added:
    case NoticeKind::Duplicate:
        return NoticeSwitch::Added;
    case NoticeKind::Error:
    case NoticeKind::AddFailed:
    case NoticeKind::DeleteFailed:
    case NoticeKind::MissingProgram:
        return NoticeSwitch::Problems;
    case NoticeKind::Background:
        return NoticeSwitch::Background;
    case NoticeKind::Failure:
    case NoticeKind::Aggregate:
        return std::nullopt;
    }
    return std::nullopt;
}

// The More menu names the Settings switch it turns off.
char const* TurnOffKey(NoticeSwitch notices)
{
    switch (notices)
    {
    case NoticeSwitch::Finished:
        return "off_finished";
    case NoticeSwitch::Added:
        return "off_added";
    case NoticeSwitch::Problems:
        return "off_problems";
    case NoticeSwitch::Background:
        return "off_background";
    }
    return "";
}

bool AcceptsNotifications()
{
    QUERY_USER_NOTIFICATION_STATE state{};
    return SUCCEEDED(SHQueryUserNotificationState(&state)) && state == QUNS_ACCEPTS_NOTIFICATIONS;
}

std::wstring Number(double value, unsigned digits = 0)
{
    wchar_t number[64];
    swprintf_s(number, L"%.*f", static_cast<int>(digits), value);
    wchar_t decimal[16] = L".";
    wchar_t thousand[16] = L",";
    wchar_t grouping[16] = L"3;0";
    LPCWSTR locale = LOCALE_NAME_USER_DEFAULT;
    GetLocaleInfoEx(locale, LOCALE_SDECIMAL, decimal, static_cast<int>(std::size(decimal)));
    GetLocaleInfoEx(locale, LOCALE_STHOUSAND, thousand, static_cast<int>(std::size(thousand)));
    GetLocaleInfoEx(locale, LOCALE_SGROUPING, grouping, static_cast<int>(std::size(grouping)));
    UINT groups = 0;
    for (auto character : std::wstring_view(grouping))
    {
        if (character == L'0') break;
        if (character != L';') groups = groups * 10 + character - L'0';
    }
    if (grouping[wcslen(grouping) - 1] != L'0') groups *= 10;
    NUMBERFMTW format{.NumDigits = digits, .LeadingZero = 1, .Grouping = groups,
        .lpDecimalSep = decimal, .lpThousandSep = thousand, .NegativeOrder = 1};
    wchar_t formatted[64];
    if (GetNumberFormatEx(locale, 0, number, &format, formatted, static_cast<int>(std::size(formatted))))
    {
        return formatted;
    }
    return number;
}
}

Tray::Tray(HWND window, UINT callback, UINT selected, HWND broadcast, Strings const& strings, bool headless)
    : window_(window), callback_(callback), broadcast_(broadcast), strings_(strings), headless_(headless),
      popup_(window, selected)
{
    if (!headless_)
    {
        errorIcon_ = ErrorIcon();
    }
}

Tray::~Tray()
{
    Apply(NIM_DELETE);
    if (errorIcon_) DestroyIcon(errorIcon_);
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
    icon.hIcon = activity_.notifiesProblems && activity_.errorCount && errorIcon_ ? errorIcon_ :
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
    bool hadError = activity_.notifiesProblems && activity_.errorCount;
    activity_ = std::move(activity);
    loading_ = loading;
    pausable_ = pausable;
    popup_.SetTheme(activity_.theme);
    if (menu_)
    {
        Rename(TrayItem::Rates, Fill(TrayItem::Rates).text);
        Rename(TrayItem::Counts, Fill(TrayItem::Counts).text);
        Rename(TrayItem::Pause, PauseText());
        EnableMenuItem(menu_, Id(TrayItem::Pause), MF_BYCOMMAND | (pausable_ ? MF_ENABLED : MF_GRAYED));
    }
    if (Tooltip() != tooltip_ || hadError != (activity_.notifiesProblems && activity_.errorCount))
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
    return strings_.Format("units", units[unit], {Number(value, unit ? 1U : 0U)});
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
        return strings_.Format("tray", count == 1 ? "paused_one" : "paused", {Number(static_cast<double>(count))});
    }
    auto active = Number(activity_.activeCount);
    auto queued = Number(activity_.queuedCount);
    return strings_.Format("tray", "counts", {active, queued});
}

std::wstring Tray::Tooltip() const
{
    auto errors = activity_.errorCount ? strings_.Format("tray", "errors", {Number(activity_.errorCount)}) + L"\n" : std::wstring();
    if (loading_)
    {
        return errors + strings_.Text("startup", "loading");
    }
    if (activity_.paused)
    {
        if (!activity_.missingAdapter.empty())
        {
            return errors + strings_.Format("tray", "missing_adapter", {Wide(activity_.missingAdapter)});
        }
        return errors + Counts();
    }
    return errors + Rates() + L"\n" + Counts();
}

std::wstring Tray::PauseText() const
{
    return strings_.Text("tray", activity_.pausedByChoice ? "resume" : "pause");
}

static bool IsFailure(NoticeKind kind)
{
    return kind == NoticeKind::Error || kind == NoticeKind::AddFailed || kind == NoticeKind::DeleteFailed ||
        kind == NoticeKind::Failure;
}

bool UsesWindow(NoticeKind kind)
{
    return kind == NoticeKind::Error || kind == NoticeKind::AddFailed || kind == NoticeKind::DeleteFailed ||
        kind == NoticeKind::Completed;
}

bool Tray::Accepts(NoticeKind kind) const
{
    bool failure = IsFailure(kind);
    bool added = kind == NoticeKind::Added || kind == NoticeKind::Duplicate;
    if (headless_ || (failure && kind != NoticeKind::Failure && !activity_.notifiesProblems) ||
        (kind == NoticeKind::MissingProgram && !activity_.notifiesProblems) ||
        (kind == NoticeKind::Completed && !activity_.notificationsEnabled) || (added && !activity_.notifiesAdded) ||
        (kind == NoticeKind::Background && !activity_.notifiesBackground))
    {
        return false;
    }
    return true;
}

void Tray::Queue(Notice notice)
{
    auto kind = notice.kind;
    if (!Accepts(kind))
    {
        return;
    }
    if (kind == NoticeKind::Background)
    {
        ShowBackground();
        return;
    }
    // One batch per kind bounds pending context while an open menu keeps its
    // popup unchanged. Different notification switches never share a batch.
    auto [entry, inserted] = batches_.try_emplace(kind, Batch{std::move(notice), 1, GetTickCount64() + noticeDelay});
    if (!inserted && kind != NoticeKind::MissingProgram)
    {
        ++entry->second.count;
    }
}

void Tray::Notify()
{
    // Let the displayed notice finish its lifetime, including hover and menu
    // interaction, before publishing another category.
    if (batches_.empty() || popup_.IsOpen())
    {
        return;
    }
    auto next = std::min_element(batches_.begin(), batches_.end(), [](auto const& left, auto const& right)
    {
        return left.second.due < right.second.due;
    });
    if (GetTickCount64() < next->second.due)
    {
        return;
    }
    auto batch = std::move(next->second);
    batches_.erase(next);
    auto kind = batch.first.kind;
    if (!Accepts(kind) || !AcceptsNotifications())
    {
        return;
    }
    notice_ = batch.count == 1 ? batch.first : Notice{NoticeKind::Aggregate};
    if (IsFailure(kind))
    {
        auto content = Failures(batch.first, batch.count);
        content.icon = errorIcon_;
        Show(std::move(content), Switch(kind));
    }
    else
    {
        Show(Message(*notice_, batch.count), Switch(kind));
    }
}

Popup::Content Tray::Message(Notice const& notice, unsigned count) const
{
    Popup::Content content{.persistent = notice.kind == NoticeKind::Failure};
    if (notice.kind == NoticeKind::Aggregate)
    {
        content.title = strings_.Format("notification", "aggregate", {Number(count)});
    }
    else if (notice.kind == NoticeKind::MissingProgram)
    {
        content.title = strings_.Text("notification", "missing_title");
        content.body = strings_.Format("notification", notice.count > 1 ? "missing_programs" : "missing_program",
            {Wide(notice.name), Number(notice.count - 1)});
    }
    else
    {
        content.title = strings_.Text("notification", ToString(notice.kind));
        content.body = notice.kind == NoticeKind::Failure ? std::wstring() : Wide(notice.name);
        auto problem = Problem(notice.code);
        if (notice.kind == NoticeKind::Failure && !notice.code.empty())
        {
            content.detail = Wide(notice.detail);
        }
        else if (!notice.detail.empty())
        {
            if (!problem.empty())
            {
                problem += L"\n";
            }
            problem += Wide(notice.detail);
        }
        if (!problem.empty())
        {
            if (!content.body.empty())
            {
                content.body += L"\n\n";
            }
            content.body += problem;
        }
    }
    return content;
}

Popup::Content Tray::Failures(Notice const& first, unsigned count) const
{
    auto notice = first;
    if (notice.kind == NoticeKind::AddFailed)
    {
        notice.name = Utf8(IsMagnet(first.name) ? strings_.Text("notification", "magnet") :
            std::filesystem::path(Wide(first.name)).filename().wstring());
    }
    auto content = Message(notice, count);
    if (count > 1)
    {
        content.title = strings_.Format("notification", "failures", {Number(count)});
    }
    return content;
}

std::wstring Tray::Problem(std::string const& code) const
{
    if (code.empty())
    {
        return {};
    }
    auto message = strings_.Text("error", code);
    if (message == Wide("error." + code))
    {
        message = strings_.Text("error", "unknown");
    }
    return message;
}

// Selecting the rest of this popup only dismisses it, because opening the
// window the person just closed would undo their action.
void Tray::ShowBackground()
{
    if (popup_.IsPersistent() || popup_.IsMenuOpen())
    {
        return;
    }
    notice_.reset();
    if (AcceptsNotifications())
    {
        Show({.title = strings_.Text("notification", "background"),
            .body = strings_.Text("tray", "background")}, NoticeSwitch::Background);
    }
}

void Tray::Show(Popup::Content content, std::optional<NoticeSwitch> notices)
{
    switch_ = notices;
    if (notices)
    {
        content.menuTitle = strings_.Text("notification", "fewer");
        // Lucide's bell-minus, bell-off and settings glyphs.
        content.menu = {
            {L'\uE1F0', strings_.Text("notification", TurnOffKey(*notices)), static_cast<UINT>(NoticeCommand::TurnOff)},
            {L'\uE05A', strings_.Text("notification", "off_all"), static_cast<UINT>(NoticeCommand::TurnOffAll)},
            {L'\uE154', strings_.Text("notification", "settings"), static_cast<UINT>(NoticeCommand::Settings)}};
    }
    popup_.Show(std::move(content));
}

std::optional<Notice> Tray::TakeNotice()
{
    return std::exchange(notice_, std::nullopt);
}

#ifdef _DEBUG
void Tray::ShowSample()
{
    notice_ = Notice{.kind = NoticeKind::Added, .name = "Sample torrent"};
    Show(Message(*notice_, 1), NoticeSwitch::Added);
}
#endif

std::optional<NoticeSwitch> Tray::TakeSwitch()
{
    return std::exchange(switch_, std::nullopt);
}
}
