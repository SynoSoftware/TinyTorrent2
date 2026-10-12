#include "Desktop/Popup.h"
#include "Engine.h"
#include "Resources.h"
#include <algorithm>
#include <cmath>
#include <sstream>
#include <windowsx.h>

namespace tt::desktop
{
namespace
{
constexpr wchar_t windowClass[] = L"TinyTorrent.Notice";
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
constexpr int buttonHeight = 32;
constexpr int buttonGap = 12;
// Space each side of the button's text, and the narrowest the button gets.
constexpr int buttonInset = 12;
constexpr int buttonWidth = 96;
constexpr int radius = 4;
// Bound long names and diagnostics so a burst cannot fill the work area.
constexpr int textLimit = 160;

// Fills of the foreground colour over the backdrop, out of 255.
constexpr BYTE restOpacity = 0x1A;
constexpr BYTE hoverOpacity = 0x2E;

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
}

Popup::~Popup()
{
    Close();
}

void Popup::Show(Content content)
{
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
    if (!content_.persistent && !tracking_)
    {
        SetTimer(window_, closeTimer, Duration(), nullptr);
    }
}

void Popup::Close()
{
    if (window_)
    {
        DestroyWindow(window_);
    }
    DeleteFonts();
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
}

void Popup::DeleteFonts()
{
    for (auto font : {&font_, &caption_, &headline_, &glyphs_})
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

    auto width = Scale(popupWidth, dpi);
    auto inset = Scale(padding, dpi);
    close_ = {width - Scale(closeInset + closeSize, dpi), Scale(closeInset, dpi), width - Scale(closeInset, dpi),
        Scale(closeInset + closeSize, dpi)};
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
    button_ = {};
    if (!content_.button.empty())
    {
        RECT label{};
        auto previous = SelectObject(device, font_);
        DrawTextW(device, content_.button.c_str(), -1, &label, DT_CALCRECT | DT_SINGLELINE | DT_NOPREFIX);
        SelectObject(device, previous);
        auto size = std::clamp<LONG>(label.right + Scale(2 * buttonInset, dpi), Scale(buttonWidth, dpi),
            width - 2 * inset);
        button_ = {width - inset - size, bottom + Scale(buttonGap, dpi), width - inset,
            bottom + Scale(buttonGap + buttonHeight, dpi)};
        bottom = button_.bottom;
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
    RECT name{header_.left + size + Scale(iconGap, dpi), header_.top, header_.right, header_.bottom};
    canvas.Text(productName, name, labelFormat, caption_);
    if (hover_ == PopupPart::Close)
    {
        canvas.Fill(close_, foreground, restOpacity, corner);
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
    if (!content_.button.empty())
    {
        canvas.Fill(button_, foreground, hover_ == PopupPart::Button ? hoverOpacity : restOpacity, corner);
        canvas.Text(content_.button, button_, labelFormat | DT_CENTER, font_);
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
    if (!content_.button.empty() && PtInRect(&button_, point))
    {
        return PopupPart::Button;
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
        if (!content_.persistent)
        {
            SetTimer(window, closeTimer, Duration(), nullptr);
        }
        return 0;
    case WM_LBUTTONUP:
    {
        auto part = Part({GET_X_LPARAM(second), GET_Y_LPARAM(second)});
        if (part != PopupPart::Close)
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
}
