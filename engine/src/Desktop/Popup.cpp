#include "Desktop/Popup.h"
#include "Engine.h"
#include "Resources.h"
#include <algorithm>
#include <cmath>
#include <filesystem>
#include <sstream>
#include <windowsx.h>

namespace tt::desktop
{
namespace
{
constexpr wchar_t windowClass[] = L"TinyTorrent.Notice";
constexpr wchar_t menuClass[] = L"TinyTorrent.NoticeMenu";
constexpr UINT_PTR closeTimer = 1;
constexpr UINT_PTR slideTimer = 2;
// The slide in from the screen's edge and back out, as Windows notifications
// arrive and leave.
constexpr ULONGLONG slideDuration = 250;
constexpr UINT slideInterval = 10;
// Seconds, when Windows does not say how long notifications stay.
constexpr ULONG defaultDuration = 5;
// Segoe MDL2 Assets' ChromeClose, which Windows 10 and 11 both have.
constexpr wchar_t closeGlyph[] = L"\xE8BB";
// Segoe MDL2 Assets' More, the button Windows' own notifications show.
constexpr wchar_t moreGlyph[] = L"\xE712";
constexpr UINT textFormat = DT_WORDBREAK | DT_EDITCONTROL | DT_NOPREFIX | DT_END_ELLIPSIS;
constexpr UINT labelFormat = DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX | DT_END_ELLIPSIS;
constexpr UINT detailFormat = DT_SINGLELINE | DT_NOPREFIX;

// Layout in effective pixels.
constexpr int popupWidth = 360;
constexpr int padding = 16;
constexpr int margin = 12;
constexpr int iconSize = 16;
constexpr int iconGap = 8;
constexpr int titleGap = 12;
constexpr int bodyGap = 8;
constexpr int detailGap = 12;
constexpr int lineGap = 4;
constexpr int fontSize = 14;
constexpr int captionSize = 12;
constexpr int closeSize = 32;
constexpr int closeInset = 8;
constexpr int glyphSize = 10;
constexpr int radius = 4;
// The More menu, sized as Windows' own notification menu.
constexpr int menuPadding = 4;
constexpr int menuRow = 36;
constexpr int menuInset = 12;
constexpr int menuIconSize = 16;
constexpr int menuIconGap = 12;
constexpr int menuGap = 4;
// The separator under the menu's title: a one-pixel line centred in it.
constexpr int menuSeparator = 9;
// Bound long names and diagnostics so a burst cannot fill the work area.
constexpr int textLimit = 160;

// The fill of the foreground colour over the backdrop under the pointer, out of 255.
constexpr BYTE hoverOpacity = 0x1A;
// Fluent's divider stroke, out of 255.
constexpr BYTE dividerOpacity = 0x14;

int Scale(int value, UINT dpi)
{
    return MulDiv(value, dpi, USER_DEFAULT_SCREEN_DPI);
}

// The monitor where Windows shows its notifications: the taskbar's.
MONITORINFO Taskbar()
{
    MONITORINFO monitor{sizeof(monitor)};
    GetMonitorInfoW(MonitorFromWindow(FindWindowW(L"Shell_TrayWnd", nullptr), MONITOR_DEFAULTTOPRIMARY),
        &monitor);
    return monitor;
}

// Windows' accessibility setting for how long notifications stay, in milliseconds.
UINT Duration()
{
    ULONG seconds = 0;
    if (!SystemParametersInfoW(SPI_GETMESSAGEDURATION, 0, &seconds, 0) || seconds == 0)
    {
        seconds = defaultDuration;
    }
    return seconds * 1000;
}

// The window ships the Lucide font beside the engine; sharing its copy keeps
// the engine small.
std::filesystem::path IconFont()
{
    return std::filesystem::path(Executable()).parent_path() / L"Lucide" / L"Fonts" / L"lucide.ttf";
}
}

Popup::Popup(HWND window, UINT selected) : owner_(window), selected_(selected)
{
    WNDCLASSW type{};
    type.lpfnWndProc = Procedure;
    type.hInstance = GetModuleHandleW(nullptr);
    type.hIcon = LoadIconW(type.hInstance, MAKEINTRESOURCEW(IDI_TINYTORRENT));
    type.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    type.lpszClassName = windowClass;
    RegisterClassW(&type);
    type.lpfnWndProc = MenuProcedure;
    type.lpszClassName = menuClass;
    RegisterClassW(&type);
    hasIcons_ = AddFontResourceExW(IconFont().c_str(), FR_PRIVATE, nullptr) > 0;
}

Popup::~Popup()
{
    Close();
    if (hasIcons_)
    {
        RemoveFontResourceExW(IconFont().c_str(), FR_PRIVATE, nullptr);
    }
}

void Popup::Show(Content content)
{
    // The menu belongs to the content it was opened for.
    if (menu_)
    {
        DestroyWindow(menu_);
    }
    content_ = std::move(content);
    if (!window_)
    {
        // Created hidden on the taskbar's monitor, so it has that monitor's
        // DPI before Layout sizes it. The frame styles keep DWM's rounded
        // corners and shadow; WM_NCCALCSIZE removes their chrome.
        auto work = Taskbar().rcWork;
        CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE, windowClass, productName,
            WS_POPUP | WS_CAPTION | WS_THICKFRAME, (work.left + work.right) / 2, (work.top + work.bottom) / 2,
            1, 1, nullptr, nullptr, GetModuleHandleW(nullptr), this);
        if (!window_)
        {
            return;
        }
    }
    auto shown = IsWindowVisible(window_) != FALSE;
    // New content stops a slide out, so the popup stays for its full time.
    KillTimer(window_, closeTimer);
    StopSlide();
    Layout();
    acrylic_.Apply(window_, theme_);
    auto text = content_.title + L"\n" + content_.body;
    if (!content_.detail.empty())
    {
        text += L"\n" + content_.detail;
    }
    SetWindowTextW(window_, text.c_str());
    if (!shown && Slides())
    {
        StartSlide(false);
    }
    ShowWindow(window_, SW_SHOWNOACTIVATE);
    // Screen readers announce the alert although the popup never takes focus.
    NotifyWinEvent(EVENT_SYSTEM_ALERT, window_, OBJID_CLIENT, CHILDID_SELF);
    ScheduleClose();
}

void Popup::Close()
{
    if (window_)
    {
        DestroyWindow(window_);
    }
    DeleteFonts();
}

void Popup::ScheduleClose()
{
    if (!content_.persistent && !tracking_ && !menu_)
    {
        SetTimer(window_, closeTimer, Duration(), nullptr);
    }
}

void Popup::SetTheme(std::string theme)
{
    if (theme == theme_)
    {
        return;
    }
    theme_ = std::move(theme);
    if (window_)
    {
        acrylic_.Apply(window_, theme_);
    }
    if (menu_)
    {
        acrylic_.Apply(menu_, theme_);
    }
}

void Popup::DeleteFonts()
{
    for (auto font : {&font_, &caption_, &headline_, &glyphs_, &icons_})
    {
        if (*font)
        {
            DeleteObject(*font);
            *font = nullptr;
        }
    }
}

// Sizes the popup to its text and places it in the corner of the work area
// beside the notification area.
void Popup::Layout()
{
    auto dpi = GetDpiForWindow(window_);
    DeleteFonts();
    NONCLIENTMETRICSW metrics{sizeof(metrics)};
    SystemParametersInfoForDpi(SPI_GETNONCLIENTMETRICS, sizeof(metrics), &metrics, 0, dpi);
    metrics.lfMessageFont.lfHeight = -Scale(captionSize, dpi);
    caption_ = CreateFontIndirectW(&metrics.lfMessageFont);
    metrics.lfMessageFont.lfHeight = -Scale(fontSize, dpi);
    font_ = CreateFontIndirectW(&metrics.lfMessageFont);
    auto headline = metrics.lfMessageFont;
    headline.lfWeight = FW_SEMIBOLD;
    headline_ = CreateFontIndirectW(&headline);
    LOGFONTW glyph{};
    glyph.lfHeight = -Scale(glyphSize, dpi);
    glyph.lfCharSet = DEFAULT_CHARSET;
    wcscpy_s(glyph.lfFaceName, L"Segoe MDL2 Assets");
    glyphs_ = CreateFontIndirectW(&glyph);
    glyph.lfHeight = -Scale(menuIconSize, dpi);
    wcscpy_s(glyph.lfFaceName, L"Lucide");
    icons_ = CreateFontIndirectW(&glyph);

    auto width = Scale(popupWidth, dpi);
    auto inset = Scale(padding, dpi);
    close_ = {width - Scale(closeInset + closeSize, dpi), Scale(closeInset, dpi), width - Scale(closeInset, dpi),
        Scale(closeInset + closeSize, dpi)};
    more_ = {close_.left - Scale(closeSize, dpi), close_.top, close_.left, close_.bottom};
    header_ = {inset, close_.top, close_.left, close_.bottom};
    auto device = GetDC(window_);
    auto measure = [&](std::wstring const& text, HFONT font, LONG top)
    {
        RECT bounds{inset, top, width - inset, top};
        if (text.empty())
        {
            return bounds;
        }
        auto previous = SelectObject(device, font);
        DrawTextW(device, text.c_str(), -1, &bounds, DT_CALCRECT | textFormat);
        SelectObject(device, previous);
        bounds.right = width - inset;
        bounds.bottom = std::min<LONG>(bounds.bottom, top + Scale(textLimit, dpi));
        return bounds;
    };
    title_ = measure(content_.title, headline_, header_.bottom + Scale(titleGap, dpi));
    body_ = measure(content_.body, font_, title_.bottom +
        (content_.body.empty() ? 0 : Scale(bodyGap, dpi)));
    auto bottom = body_.bottom;
    detail_ = {};
    if (!content_.detail.empty())
    {
        auto previous = SelectObject(device, caption_);
        TEXTMETRICW metrics{};
        GetTextMetricsW(device, &metrics);
        SelectObject(device, previous);
        auto lines = 1 + std::count(content_.detail.begin(), content_.detail.end(), L'\n');
        auto height = std::min<LONG>(static_cast<LONG>(lines) * (metrics.tmHeight + Scale(lineGap, dpi)) -
            Scale(lineGap, dpi), Scale(textLimit, dpi));
        detail_ = {inset, bottom + Scale(detailGap, dpi), width - inset,
            bottom + Scale(detailGap, dpi) + height};
        bottom = detail_.bottom;
    }
    ReleaseDC(window_, device);
    auto height = bottom + inset;
    auto work = Taskbar().rcWork;
    place_ = {work.right - Scale(margin, dpi) - width, work.bottom - Scale(margin, dpi) - height};
    // A slide keeps moving the popup toward or away from its new place.
    auto move = slideStart_ ? SWP_NOMOVE : 0;
    SetWindowPos(window_, HWND_TOPMOST, place_.x, place_.y, width, height, SWP_NOACTIVATE | move);
    InvalidateRect(window_, nullptr, FALSE);
}

// Windows' animation effects setting stills the popup, as it stills Windows'
// own notifications. A popup that would slide onto a monitor beyond the
// taskbar's right edge appears without the slide.
bool Popup::Slides() const
{
    BOOL animates = TRUE;
    SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION, 0, &animates, 0);
    if (!animates)
    {
        return false;
    }
    auto monitor = Taskbar();
    RECT bounds{};
    GetWindowRect(window_, &bounds);
    RECT path{monitor.rcMonitor.right, place_.y, monitor.rcWork.right + bounds.right - bounds.left,
        place_.y + bounds.bottom - bounds.top};
    return IsRectEmpty(&path) || !MonitorFromRect(&path, MONITOR_DEFAULTTONULL);
}

void Popup::StartSlide(bool leaving)
{
    leaving_ = leaving;
    slideStart_ = GetTickCount64();
    Slide();
    SetTimer(window_, slideTimer, slideInterval, nullptr);
}

void Popup::StopSlide()
{
    KillTimer(window_, slideTimer);
    slideStart_ = 0;
    leaving_ = false;
}

// Moves the popup between its place and beyond the work area's edge, easing
// toward rest as Windows' own motion does. A popup that has left closes.
void Popup::Slide()
{
    auto elapsed = GetTickCount64() - slideStart_;
    auto progress = std::min(1.0, static_cast<double>(elapsed) / slideDuration);
    auto away = leaving_ ? progress * progress * progress : std::pow(1 - progress, 3);
    auto distance = Taskbar().rcWork.right - place_.x;
    SetWindowPos(window_, nullptr, place_.x + static_cast<int>(distance * away), place_.y, 0, 0,
        SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    if (progress < 1)
    {
        return;
    }
    if (leaving_)
    {
        Close();
        return;
    }
    StopSlide();
}

void Popup::Paint(HWND window)
{
    Acrylic::Canvas canvas(acrylic_, window);
    auto dpi = GetDpiForWindow(window);
    auto corner = Scale(radius, dpi);
    auto foreground = acrylic_.Foreground();
    auto size = Scale(iconSize, dpi);
    auto icon = content_.icon ? content_.icon : static_cast<HICON>(LoadImageW(GetModuleHandleW(nullptr),
        MAKEINTRESOURCEW(IDI_TINYTORRENT), IMAGE_ICON, size, size, LR_SHARED));
    DrawIconEx(canvas.Device(), header_.left, (header_.top + header_.bottom - size) / 2, icon, size, size, 0,
        nullptr, DI_NORMAL);
    // As in Windows' own notifications, More shows only while the pointer is
    // over the popup, so the message is all the person reads.
    auto showsMore = !content_.menu.empty() && (hover_ || menu_);
    RECT name{header_.left + size + Scale(iconGap, dpi), header_.top, showsMore ? more_.left : header_.right,
        header_.bottom};
    canvas.Text(productName, name, labelFormat, caption_);
    if (showsMore)
    {
        if (hover_ == PopupPart::More || menu_)
        {
            canvas.Fill(more_, foreground, hoverOpacity, corner);
        }
        canvas.Text(moreGlyph, more_, DT_SINGLELINE | DT_CENTER | DT_VCENTER, glyphs_);
    }
    if (hover_ == PopupPart::Close)
    {
        canvas.Fill(close_, foreground, hoverOpacity, corner);
    }
    canvas.Text(closeGlyph, close_, DT_SINGLELINE | DT_CENTER | DT_VCENTER, glyphs_);
    canvas.Text(content_.title, title_, textFormat, headline_);
    canvas.Text(content_.body, body_, textFormat, font_);
    auto previous = SelectObject(canvas.Device(), caption_);
    TEXTMETRICW metrics{};
    GetTextMetricsW(canvas.Device(), &metrics);
    SelectObject(canvas.Device(), previous);
    std::wistringstream details(content_.detail);
    auto bounds = detail_;
    for (std::wstring line; std::getline(details, line) && bounds.top + metrics.tmHeight <= detail_.bottom;)
    {
        bounds.bottom = bounds.top + metrics.tmHeight;
        auto ellipsis = line.find(L'\\') == std::wstring::npos ? DT_END_ELLIPSIS : DT_PATH_ELLIPSIS;
        canvas.Text(line, bounds, detailFormat | ellipsis, caption_);
        bounds.top = bounds.bottom + Scale(lineGap, dpi);
    }
}

void Popup::Hover(std::optional<PopupPart> part)
{
    if (part != hover_)
    {
        hover_ = part;
        InvalidateRect(window_, nullptr, FALSE);
    }
}

PopupPart Popup::Part(POINT point) const
{
    if (PtInRect(&close_, point))
    {
        return PopupPart::Close;
    }
    if (!content_.menu.empty() && PtInRect(&more_, point))
    {
        return PopupPart::More;
    }
    return PopupPart::Body;
}

LRESULT CALLBACK Popup::Procedure(HWND window, UINT message, WPARAM first, LPARAM second)
{
    auto owner = reinterpret_cast<Popup*>(GetWindowLongPtrW(window, GWLP_USERDATA));
    if (message == WM_NCCREATE)
    {
        owner = static_cast<Popup*>(reinterpret_cast<CREATESTRUCTW*>(second)->lpCreateParams);
        owner->window_ = window;
        SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(owner));
    }
    return owner ? owner->Handle(window, message, first, second) :
        DefWindowProcW(window, message, first, second);
}

LRESULT Popup::Handle(HWND window, UINT message, WPARAM first, LPARAM second)
{
    switch (message)
    {
    case WM_TIMER:
        if (first == closeTimer)
        {
            KillTimer(window, closeTimer);
            if (content_.persistent)
            {
                return 0;
            }
            if (Slides())
            {
                StartSlide(true);
            }
            else
            {
                Close();
            }
        }
        else if (first == slideTimer && slideStart_)
        {
            Slide();
        }
        return 0;
    case WM_MOUSEACTIVATE:
        return MA_NOACTIVATE;
    case WM_MOUSEMOVE:
        // The popup stays while the pointer is over it, so it can be read.
        if (!tracking_)
        {
            TRACKMOUSEEVENT track{sizeof(track), TME_LEAVE, window, 0};
            tracking_ = TrackMouseEvent(&track) != FALSE;
            KillTimer(window, closeTimer);
        }
        if (leaving_)
        {
            StopSlide();
            SetWindowPos(window, nullptr, place_.x, place_.y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
        Hover(Part({GET_X_LPARAM(second), GET_Y_LPARAM(second)}));
        return 0;
    case WM_MOUSELEAVE:
        tracking_ = false;
        Hover(std::nullopt);
        ScheduleClose();
        return 0;
    case WM_LBUTTONUP:
    {
        auto part = Part({GET_X_LPARAM(second), GET_Y_LPARAM(second)});
        // The More button toggles its menu, which a click on this popup does
        // not close, because the popup never takes the focus.
        if (part == PopupPart::More)
        {
            if (menu_)
            {
                DestroyWindow(menu_);
            }
            else
            {
                OpenMenu();
            }
            return 0;
        }
        if (part == PopupPart::Body)
        {
            PostMessageW(owner_, selected_, static_cast<WPARAM>(part), 0);
        }
        Close();
        return 0;
    }
    case WM_NCCALCSIZE:
        return 0;
    case WM_NCHITTEST:
        return HTCLIENT;
    case WM_ERASEBKGND:
        return 0;
    case WM_PAINT:
        Paint(window);
        return 0;
    case WM_DPICHANGED:
    case WM_SETTINGCHANGE:
        Layout();
        acrylic_.Apply(window, theme_);
        return 0;
    case WM_SYSCOLORCHANGE:
    case WM_THEMECHANGED:
    case WM_DWMCOMPOSITIONCHANGED:
        acrylic_.Apply(window, theme_);
        return 0;
    case WM_NCDESTROY:
        window_ = nullptr;
        tracking_ = false;
        hover_.reset();
        slideStart_ = 0;
        leaving_ = false;
        break;
    }
    return DefWindowProcW(window, message, first, second);
}

// The menu opens above the More button, aligned to its right edge, as
// Windows' own notification menu does, or below it when the work area leaves
// no room above. It takes the focus, so Escape and a click elsewhere close it.
void Popup::OpenMenu()
{
    auto dpi = GetDpiForWindow(window_);
    auto device = GetDC(window_);
    auto previous = SelectObject(device, font_);
    auto measure = [device](std::wstring const& text)
    {
        RECT bounds{};
        DrawTextW(device, text.c_str(), -1, &bounds, DT_CALCRECT | DT_SINGLELINE | DT_NOPREFIX);
        return bounds.right;
    };
    auto widest = measure(content_.menuTitle);
    for (auto const& item : content_.menu)
    {
        widest = std::max(widest, measure(item.text));
    }
    SelectObject(device, previous);
    ReleaseDC(window_, device);
    menuRow_ = Scale(menuRow, dpi);
    auto width = widest + Scale(2 * menuPadding + 2 * menuInset, dpi) + MenuIconsWidth(dpi);
    auto height = MenuItemsTop(dpi) + static_cast<int>(content_.menu.size()) * menuRow_ + Scale(menuPadding, dpi);
    auto anchor = more_;
    MapWindowPoints(window_, nullptr, reinterpret_cast<POINT*>(&anchor), 2);
    auto work = Taskbar().rcWork;
    auto x = std::max<LONG>(work.left, anchor.right - width);
    auto y = anchor.top - Scale(menuGap, dpi) - height;
    if (y < work.top)
    {
        y = anchor.bottom + Scale(menuGap, dpi);
    }
    // The frame styles keep DWM's rounded corners and shadow, as on the popup.
    CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_TOPMOST, menuClass, productName, WS_POPUP | WS_CAPTION | WS_THICKFRAME,
        x, y, width, height, window_, nullptr, GetModuleHandleW(nullptr), this);
    if (!menu_)
    {
        return;
    }
    KillTimer(window_, closeTimer);
    acrylic_.Apply(menu_, theme_);
    ShowWindow(menu_, SW_SHOW);
    SetForegroundWindow(menu_);
    InvalidateRect(window_, nullptr, FALSE);
}

void Popup::PaintMenu(HWND window)
{
    Acrylic::Canvas canvas(acrylic_, window);
    auto dpi = GetDpiForWindow(window);
    auto padding = Scale(menuPadding, dpi);
    auto inset = Scale(menuInset, dpi);
    auto width = canvas.Bounds().right;
    // The title lines up with the items' text, as in Windows' menu.
    RECT title{padding + inset + MenuIconsWidth(dpi), padding, width - padding - inset, padding + menuRow_};
    canvas.Text(content_.menuTitle, title, labelFormat, font_);
    auto line = title.bottom + Scale(menuSeparator, dpi) / 2;
    canvas.Fill({0, line, width, line + Scale(1, dpi)}, acrylic_.Foreground(), dividerOpacity, 0);
    auto itemsTop = MenuItemsTop(dpi);
    for (std::size_t index = 0; index < content_.menu.size(); ++index)
    {
        auto const& item = content_.menu[index];
        auto top = itemsTop + static_cast<int>(index) * menuRow_;
        RECT row{padding, top, width - padding, top + menuRow_};
        if (menuHover_ == index)
        {
            canvas.Fill(row, acrylic_.Foreground(), hoverOpacity, Scale(radius, dpi));
        }
        auto left = row.left + inset;
        if (hasIcons_)
        {
            RECT icon{left, row.top, left + Scale(menuIconSize, dpi), row.bottom};
            canvas.Text(std::wstring(1, item.glyph), icon, DT_SINGLELINE | DT_CENTER | DT_VCENTER, icons_);
        }
        RECT text{left + MenuIconsWidth(dpi), row.top, row.right - inset, row.bottom};
        canvas.Text(item.text, text, labelFormat, font_);
    }
}

int Popup::MenuItemsTop(UINT dpi) const
{
    return Scale(menuPadding + menuSeparator, dpi) + menuRow_;
}

int Popup::MenuIconsWidth(UINT dpi) const
{
    return hasIcons_ ? Scale(menuIconSize + menuIconGap, dpi) : 0;
}

std::optional<std::size_t> Popup::MenuItemAt(POINT point) const
{
    RECT bounds{};
    GetClientRect(menu_, &bounds);
    auto dpi = GetDpiForWindow(menu_);
    auto padding = Scale(menuPadding, dpi);
    auto top = MenuItemsTop(dpi);
    if (point.x < padding || point.x >= bounds.right - padding || point.y < top)
    {
        return std::nullopt;
    }
    auto item = static_cast<std::size_t>((point.y - top) / menuRow_);
    if (item >= content_.menu.size())
    {
        return std::nullopt;
    }
    return item;
}

void Popup::HoverMenu(std::optional<std::size_t> item)
{
    if (item != menuHover_)
    {
        menuHover_ = item;
        InvalidateRect(menu_, nullptr, FALSE);
    }
}

void Popup::Choose(std::size_t item)
{
    PostMessageW(owner_, selected_, static_cast<WPARAM>(PopupPart::More), content_.menu[item].id);
    Close();
}

LRESULT CALLBACK Popup::MenuProcedure(HWND window, UINT message, WPARAM first, LPARAM second)
{
    auto owner = reinterpret_cast<Popup*>(GetWindowLongPtrW(window, GWLP_USERDATA));
    if (message == WM_NCCREATE)
    {
        owner = static_cast<Popup*>(reinterpret_cast<CREATESTRUCTW*>(second)->lpCreateParams);
        owner->menu_ = window;
        SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(owner));
    }
    return owner ? owner->HandleMenu(window, message, first, second) :
        DefWindowProcW(window, message, first, second);
}

LRESULT Popup::HandleMenu(HWND window, UINT message, WPARAM first, LPARAM second)
{
    switch (message)
    {
    case WM_MOUSEMOVE:
    {
        TRACKMOUSEEVENT track{sizeof(track), TME_LEAVE, window, 0};
        TrackMouseEvent(&track);
        HoverMenu(MenuItemAt({GET_X_LPARAM(second), GET_Y_LPARAM(second)}));
        return 0;
    }
    case WM_MOUSELEAVE:
        HoverMenu(std::nullopt);
        return 0;
    case WM_LBUTTONUP:
        if (auto item = MenuItemAt({GET_X_LPARAM(second), GET_Y_LPARAM(second)}))
        {
            Choose(*item);
        }
        return 0;
    case WM_KEYDOWN:
    {
        auto count = content_.menu.size();
        if (first == VK_ESCAPE)
        {
            DestroyWindow(window);
        }
        else if (first == VK_DOWN)
        {
            HoverMenu(menuHover_ ? (*menuHover_ + 1) % count : 0);
        }
        else if (first == VK_UP)
        {
            HoverMenu(menuHover_ ? (*menuHover_ + count - 1) % count : count - 1);
        }
        else if ((first == VK_RETURN || first == VK_SPACE) && menuHover_)
        {
            Choose(*menuHover_);
        }
        return 0;
    }
    case WM_ACTIVATE:
        // A click elsewhere closes the menu, as it closes Windows' own.
        if (LOWORD(first) == WA_INACTIVE)
        {
            PostMessageW(window, WM_CLOSE, 0, 0);
        }
        break;
    case WM_NCCALCSIZE:
        return 0;
    case WM_NCHITTEST:
        return HTCLIENT;
    case WM_ERASEBKGND:
        return 0;
    case WM_PAINT:
        PaintMenu(window);
        return 0;
    case WM_SETTINGCHANGE:
    case WM_SYSCOLORCHANGE:
    case WM_THEMECHANGED:
    case WM_DWMCOMPOSITIONCHANGED:
        acrylic_.Apply(window, theme_);
        return 0;
    case WM_NCDESTROY:
        menu_ = nullptr;
        menuHover_.reset();
        if (window_)
        {
            ScheduleClose();
            InvalidateRect(window_, nullptr, FALSE);
        }
        break;
    }
    return DefWindowProcW(window, message, first, second);
}
}
