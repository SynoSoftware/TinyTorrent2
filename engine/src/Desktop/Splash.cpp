#include "Desktop/Splash.h"
#include "Resources.h"
#include <algorithm>
#include <numeric>
#include <utility>

namespace tt::desktop
{
namespace
{
constexpr wchar_t windowClass[] = L"TinyTorrent.Startup";
constexpr UINT_PTR dwellTimer = 1;
constexpr UINT minimumDwell = 1000;
// The recent warm launches whose average predicts the next one.
constexpr std::size_t launchSamples = 5;
// Layout in effective pixels.
constexpr SIZE openingSize{350, 170};
constexpr int iconSize = 48;
constexpr int iconGap = 12;
constexpr int textInset = 16;

int Scale(int value, UINT dpi)
{
    return MulDiv(value, dpi, USER_DEFAULT_SCREEN_DPI);
}
}

Splash::Splash(Strings const& strings) : strings_(strings)
{
    WNDCLASSW type{};
    type.lpfnWndProc = Procedure;
    type.hInstance = GetModuleHandleW(nullptr);
    type.hIcon = LoadIconW(type.hInstance, MAKEINTRESOURCEW(IDI_TINYTORRENT));
    type.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    type.lpszClassName = windowClass;
    RegisterClassW(&type);
}

Splash::~Splash()
{
    // The application and its pipe have already released the reply's owners.
    show_ = nullptr;
    Close();
}

// A cold launch also waits for the engine to load, so it always shows the
// splash. A warm launch shows it unless recent warm launches were ready
// within the splash's minimum time, which avoids a splash that only delays
// the window.
void Splash::Show(Launch launch)
{
    if (window_)
    {
        return;
    }
    if (launch == Launch::Warm && !launches_.empty() &&
        std::accumulate(launches_.begin(), launches_.end(), ULONGLONG{0}) / launches_.size() <= minimumDwell)
    {
        return;
    }
    POINT point{};
    GetCursorPos(&point);
    MONITORINFO monitor{sizeof(monitor)};
    GetMonitorInfoW(MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST), &monitor);
    // Retain the frame styles for DWM rounding; WM_NCCALCSIZE removes their chrome.
    window_ = CreateWindowExW(WS_EX_TOOLWINDOW, windowClass, productName,
        WS_POPUP | WS_CAPTION | WS_THICKFRAME, point.x, point.y, openingSize.cx, openingSize.cy,
        nullptr, nullptr, GetModuleHandleW(nullptr), this);
    if (!window_)
    {
        return;
    }
    auto dpi = GetDpiForWindow(window_);
    auto width = Scale(openingSize.cx, dpi);
    auto height = Scale(openingSize.cy, dpi);
    auto const& work = monitor.rcWork;
    SetWindowPos(window_, nullptr, work.left + (work.right - work.left - width) / 2,
        work.top + (work.bottom - work.top - height) / 2, width, height,
        SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    ApplyDpi(dpi, nullptr);
    acrylic_.Apply(window_, theme_);
    Translate();
    shownAt_ = GetTickCount64();
    ShowWindow(window_, SW_SHOW);
    // ShowWindow brings only a new process's first window to the front. A
    // running engine uses the foreground right that the tray click or the
    // forwarding launch granted it.
    SetForegroundWindow(window_);
    // WM_PAINT waits until no other message is queued.
    UpdateWindow(window_);
}

// The time from starting a warm launch's window process until it was ready.
void Splash::Record(ULONGLONG duration)
{
    launches_.push_back(duration);
    if (launches_.size() > launchSamples)
    {
        launches_.pop_front();
    }
}

// The product window is drawn and waits to appear. A splash is seen for its
// minimum time before `show` lets the window appear, and stays until the
// window has appeared over it.
void Splash::Finish(std::function<void()> show)
{
    if (!shownAt_)
    {
        show();
        return;
    }
    auto elapsed = GetTickCount64() - shownAt_;
    if (elapsed >= minimumDwell)
    {
        show();
        return;
    }
    show_ = std::move(show);
    SetTimer(window_, dwellTimer, static_cast<UINT>(minimumDwell - elapsed), nullptr);
}

void Splash::Close()
{
    if (window_)
    {
        DestroyWindow(window_);
    }
    if (font_)
    {
        DeleteObject(font_);
        font_ = nullptr;
    }
    shownAt_ = 0;
    if (auto show = std::exchange(show_, nullptr))
    {
        show();
    }
}

void Splash::Translate()
{
    if (window_)
    {
        auto text = strings_.Text("startup", "opening");
        SetWindowTextW(window_, text.c_str());
        InvalidateRect(window_, nullptr, FALSE);
    }
}

// The splash follows the app's theme choice, like the window that replaces it.
void Splash::SetTheme(Theme theme)
{
    if (theme == theme_)
    {
        return;
    }
    theme_ = theme;
    if (shownAt_)
    {
        acrylic_.Apply(window_, theme_);
    }
}

void Splash::ApplyDpi(UINT dpi, RECT const* bounds)
{
    NONCLIENTMETRICSW metrics{sizeof(metrics)};
    SystemParametersInfoForDpi(SPI_GETNONCLIENTMETRICS, sizeof(metrics), &metrics, 0, dpi);
    if (font_)
    {
        DeleteObject(font_);
    }
    font_ = CreateFontIndirectW(&metrics.lfMessageFont);
    if (bounds)
    {
        SetWindowPos(window_, nullptr, bounds->left, bounds->top, bounds->right - bounds->left,
            bounds->bottom - bounds->top, SWP_NOZORDER | SWP_NOACTIVATE);
    }
    InvalidateRect(window_, nullptr, FALSE);
}

void Splash::Paint(HWND window)
{
    Acrylic::Canvas canvas(acrylic_, window);
    auto const& bounds = canvas.Bounds();
    auto target = canvas.Device();
    auto dpi = GetDpiForWindow(window);
    auto text = strings_.Text("startup", "opening");
    RECT measured{0, 0, bounds.right - Scale(2 * textInset, dpi), 0};
    auto previous = SelectObject(target, font_);
    DrawTextW(target, text.c_str(), -1, &measured, DT_CALCRECT | DT_WORDBREAK | DT_NOPREFIX);
    SelectObject(target, previous);
    auto size = Scale(iconSize, dpi);
    auto gap = Scale(iconGap, dpi);
    auto top = std::max<LONG>(0, (bounds.bottom - size - gap - measured.bottom) / 2);
    auto icon = LoadImageW(GetModuleHandleW(nullptr), MAKEINTRESOURCEW(IDI_TINYTORRENT),
        IMAGE_ICON, size, size, LR_SHARED);
    DrawIconEx(target, (bounds.right - size) / 2, top, static_cast<HICON>(icon),
        size, size, 0, nullptr, DI_NORMAL);
    RECT textBounds{Scale(textInset, dpi), top + size + gap,
        bounds.right - Scale(textInset, dpi), bounds.bottom};
    canvas.Text(text, textBounds, DT_CENTER | DT_WORDBREAK | DT_NOPREFIX, font_, acrylic_.Foreground());
}

LRESULT CALLBACK Splash::Procedure(HWND window, UINT message, WPARAM first, LPARAM second)
{
    auto owner = reinterpret_cast<Splash*>(GetWindowLongPtrW(window, GWLP_USERDATA));
    if (message == WM_NCCREATE)
    {
        owner = static_cast<Splash*>(reinterpret_cast<CREATESTRUCTW*>(second)->lpCreateParams);
        owner->window_ = window;
        SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(owner));
    }
    return owner ? owner->Handle(window, message, first, second) :
        DefWindowProcW(window, message, first, second);
}

LRESULT Splash::Handle(HWND window, UINT message, WPARAM first, LPARAM second)
{
    switch (message)
    {
    case WM_TIMER:
        if (first == dwellTimer)
        {
            KillTimer(window, dwellTimer);
            std::exchange(show_, nullptr)();
        }
        return 0;
    case WM_NCCALCSIZE:
        return 0;
    case WM_NCHITTEST:
        return HTCLIENT;
    case WM_CLOSE:
    case WM_ERASEBKGND:
        return 0;
    case WM_PAINT:
        Paint(window);
        return 0;
    case WM_DPICHANGED:
        ApplyDpi(LOWORD(first), reinterpret_cast<RECT const*>(second));
        return 0;
    case WM_SETTINGCHANGE:
    case WM_SYSCOLORCHANGE:
    case WM_THEMECHANGED:
    case WM_DWMCOMPOSITIONCHANGED:
        acrylic_.Apply(window, theme_);
        return 0;
    case WM_NCDESTROY:
        window_ = nullptr;
        break;
    }
    return DefWindowProcW(window, message, first, second);
}
}
