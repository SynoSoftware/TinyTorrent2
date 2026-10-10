#include "Desktop/Tray.h"
#include "Resources.h"
#include <algorithm>
#include <filesystem>
#include <roapi.h>
#include <shellapi.h>
#include <windows.data.xml.dom.h>
#include <windows.ui.notifications.h>
#include <wrl/client.h>
#include <wrl/event.h>
#include <wrl/wrappers/corewrappers.h>

namespace tt::desktop
{
namespace
{
using Microsoft::WRL::ComPtr;
using Microsoft::WRL::Wrappers::HString;
using Microsoft::WRL::Wrappers::HStringReference;
namespace notifications = ABI::Windows::UI::Notifications;
namespace xml = ABI::Windows::Data::Xml::Dom;

constexpr UINT iconId = 1;
// The toast button's activation argument.
constexpr wchar_t backgroundOffArgument[] = L"background_off";
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

std::wstring EscapeXml(std::wstring const& text)
{
    std::wstring escaped;
    for (auto character : text)
    {
        switch (character)
        {
        case L'&': escaped += L"&amp;"; break;
        case L'<': escaped += L"&lt;"; break;
        case L'>': escaped += L"&gt;"; break;
        case L'"': escaped += L"&quot;"; break;
        default: escaped += character; break;
        }
    }
    return escaped;
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
    auto locale = LOCALE_NAME_USER_DEFAULT;
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

// A Windows toast notification. Unlike a balloon it can carry a button, so
// the background notice uses it to let the person turn that notice off
// without opening the window. Windows needs the AppUserModelID registered to
// show it; the installer's Start menu shortcut registers it.
class Tray::Toast
{
public:
    // Toast objects are COM objects, used from the owner window's thread.
    Toast() : initialized_(SUCCEEDED(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED))) {}

    // Removes the shown toast, so its button never outlives the engine.
    ~Toast()
    {
        Hide();
        shown_.Reset();
        notifier_.Reset();
        if (initialized_)
        {
            CoUninitialize();
        }
    }

    Toast(Toast const&) = delete;
    Toast& operator=(Toast const&) = delete;

    // Replaces the shown toast. Selecting `button` posts `message` to
    // `window`; selecting the rest of the toast only dismisses it. Returns
    // whether Windows accepted the toast.
    bool Show(std::wstring const& text, std::wstring const& button, HWND window, UINT message)
    {
        if (!notifier_)
        {
            ComPtr<notifications::IToastNotificationManagerStatics> manager;
            if (FAILED(RoGetActivationFactory(
                    HStringReference(RuntimeClass_Windows_UI_Notifications_ToastNotificationManager).Get(),
                    IID_PPV_ARGS(manager.GetAddressOf()))) ||
                FAILED(manager->CreateToastNotifierWithId(HStringReference(appId).Get(), notifier_.GetAddressOf())))
            {
                notifier_.Reset();
                return false;
            }
        }
        auto content = std::wstring(L"<toast><visual><binding template=\"ToastGeneric\"><text>") + productName +
            L"</text><text>" + EscapeXml(text) + L"</text></binding></visual><actions><action content=\"" +
            EscapeXml(button) + L"\" arguments=\"" + backgroundOffArgument + L"\"/></actions></toast>";
        ComPtr<IInspectable> instance;
        ComPtr<xml::IXmlDocumentIO> reader;
        ComPtr<xml::IXmlDocument> document;
        ComPtr<notifications::IToastNotificationFactory> factory;
        ComPtr<notifications::IToastNotification> toast;
        EventRegistrationToken token{};
        if (FAILED(RoActivateInstance(HStringReference(RuntimeClass_Windows_Data_Xml_Dom_XmlDocument).Get(),
                instance.GetAddressOf())) ||
            FAILED(instance.As(&reader)) ||
            FAILED(reader->LoadXml(HStringReference(content.c_str(), static_cast<unsigned>(content.size())).Get())) ||
            FAILED(instance.As(&document)) ||
            FAILED(RoGetActivationFactory(
                HStringReference(RuntimeClass_Windows_UI_Notifications_ToastNotification).Get(),
                IID_PPV_ARGS(factory.GetAddressOf()))) ||
            FAILED(factory->CreateToastNotification(document.Get(), toast.GetAddressOf())) ||
            FAILED(toast->add_Activated(
                Microsoft::WRL::Callback<
                    __FITypedEventHandler_2_Windows__CUI__CNotifications__CToastNotification_IInspectable>(
                    [window, message](notifications::IToastNotification*, IInspectable* arguments) -> HRESULT
                    {
                        // Windows calls this on a thread of its own.
                        ComPtr<notifications::IToastActivatedEventArgs> activated;
                        HString chosen;
                        if (arguments && SUCCEEDED(arguments->QueryInterface(IID_PPV_ARGS(activated.GetAddressOf()))) &&
                            SUCCEEDED(activated->get_Arguments(chosen.GetAddressOf())) &&
                            wcscmp(chosen.GetRawBuffer(nullptr), backgroundOffArgument) == 0)
                        {
                            PostMessageW(window, message, 0, 0);
                        }
                        return S_OK;
                    }).Get(),
                &token)))
        {
            return false;
        }
        Hide();
        if (FAILED(notifier_->Show(toast.Get())))
        {
            return false;
        }
        shown_ = toast;
        return true;
    }

private:
    void Hide()
    {
        if (notifier_ && shown_)
        {
            notifier_->Hide(shown_.Get());
        }
        shown_.Reset();
    }

    bool initialized_;
    ComPtr<notifications::IToastNotifier> notifier_;
    ComPtr<notifications::IToastNotification> shown_;
};

Tray::Tray(HWND window, UINT callback, UINT backgroundOff, HWND broadcast, Strings const& strings, bool headless)
    : window_(window), callback_(callback), backgroundOff_(backgroundOff), broadcast_(broadcast), strings_(strings),
      headless_(headless)
{
    if (!headless_)
    {
        errorIcon_ = ErrorIcon();
        toast_ = std::make_unique<Toast>();
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
        return strings_.Format("tray", count == 1 ? "paused_one" : "paused", {Number(count)});
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

bool UsesWindow(NoticeKind kind)
{
    return kind == NoticeKind::Error || kind == NoticeKind::AddFailed || kind == NoticeKind::DeleteFailed ||
        kind == NoticeKind::Completed;
}

void Tray::Queue(Notice notice)
{
    auto kind = notice.kind;
    bool failure = kind == NoticeKind::Error || kind == NoticeKind::AddFailed || kind == NoticeKind::DeleteFailed ||
        kind == NoticeKind::Failure;
    bool added = kind == NoticeKind::Added || kind == NoticeKind::Duplicate;
    if (headless_ || (failure && kind != NoticeKind::Failure && !activity_.notifiesProblems) ||
        (kind == NoticeKind::MissingProgram && !activity_.notifiesProblems) ||
        (kind == NoticeKind::Completed && !activity_.notificationsEnabled) || (added && !activity_.notifiesAdded) ||
        (kind == NoticeKind::Background && !activity_.notifiesBackground))
    {
        return;
    }
    if (kind == NoticeKind::Background)
    {
        ShowBackground();
        return;
    }
    // A failure balloon names only the failures, so a batch never mixes
    // notices for the window with tray-only ones. A missing program is shown
    // once and its balloon opens Settings, so it never joins a batch.
    if (batch_.first && (UsesWindow(batch_.first->kind) != UsesWindow(kind) ||
        batch_.first->kind == NoticeKind::MissingProgram || kind == NoticeKind::MissingProgram))
    {
        Flush();
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

void Tray::Notify()
{
    if (batch_.due && GetTickCount64() >= batch_.due)
    {
        Flush();
    }
}

void Tray::Flush()
{
    auto batch = std::exchange(batch_, {});
    notice_ = batch.count == 1 ? std::move(*batch.first) : Notice{NoticeKind::Aggregate};
    if (!AcceptsNotifications())
    {
        notice_.reset();
    }
    else if (batch.failure)
    {
        Balloon(Failures(*batch.failure, batch.failureCount), true);
    }
    else
    {
        Balloon(Message(*notice_, batch.count), false);
    }
}

std::wstring Tray::Message(Notice const& notice, unsigned count) const
{
    std::wstring message;
    if (notice.kind == NoticeKind::Aggregate)
    {
        message = strings_.Format("notification", "aggregate", {Number(count)});
    }
    else if (notice.kind == NoticeKind::MissingProgram && notice.count > 1)
    {
        message = strings_.Format("notification", "missing_programs",
            {Wide(notice.name), Number(notice.count - 1)});
    }
    else
    {
        message = strings_.Format("notification", ToString(notice.kind), {Wide(notice.name)});
    }
    auto detail = Detail(notice);
    if (!detail.empty())
    {
        message += L"\n" + detail;
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
    auto detail = Detail(first);
    return strings_.Format("notification", "failures",
        {Number(count), name.substr(0, nameLength), detail.substr(0, detailLength)});
}

std::wstring Tray::Detail(Notice const& notice) const
{
    auto detail = Wide(notice.detail);
    if (notice.code.empty())
    {
        return detail;
    }
    auto message = strings_.Text("error", notice.code);
    if (message == Wide("error." + notice.code))
    {
        message = strings_.Text("error", "unknown");
    }
    return detail.empty() ? message : message + L"\n" + detail;
}

// When Windows refuses the toast, the notice still shows as a balloon, which
// has no button.
void Tray::ShowBackground()
{
    auto message = strings_.Text("tray", "background");
    if (toast_->Show(message, strings_.Text("tray", "background_off"), window_, backgroundOff_))
    {
        return;
    }
    // A balloon replaces the shown one, so its click belongs to this notice.
    notice_.reset();
    if (AcceptsNotifications())
    {
        notice_ = Notice{NoticeKind::Background};
        Balloon(message, false);
    }
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

std::optional<Notice> Tray::TakeNotice()
{
    return std::exchange(notice_, std::nullopt);
}
}
