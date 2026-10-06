#include "Desktop/Splash.h"
#include "Resources.h"
#include <algorithm>
#include <utility>
#include <dwmapi.h>
#include <uxtheme.h>

namespace tt::desktop
{
namespace
{
constexpr wchar_t windowClass[] = L"TinyTorrent.Startup";
constexpr UINT_PTR showTimer = 1;
constexpr UINT_PTR dwellTimer = 2;
// A cold launch waits for the engine to load, so its splash shows at once; a
// warm window that is ready within the delay appears without one.
constexpr UINT coldDelay = 0;
constexpr UINT warmDelay = 400;
constexpr UINT minimumDwell = 1000;
// The blur shows through a layer of the theme's background at this opacity, so
// the text keeps its contrast in either theme.
constexpr BYTE tintOpacity = 0x99;

// Layout in effective pixels.
constexpr SIZE openingSize{350, 170};
constexpr int iconSize = 48;
constexpr int iconGap = 12;
constexpr int textInset = 16;

int Scale(int value, UINT dpi)
{
    return MulDiv(value, dpi, USER_DEFAULT_SCREEN_DPI);
}

bool Preference(wchar_t const* name)
{
    DWORD value = 1;
    DWORD size = sizeof(value);
    auto status = RegGetValueW(HKEY_CURRENT_USER,
        L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize", name,
        RRF_RT_REG_DWORD, nullptr, &value, &size);
    return status != ERROR_SUCCESS || value != 0;
}

// The blur of the original TinyTorrent splash. User32 exports
// SetWindowCompositionAttribute without documenting it; the documented DWM
// system backdrop left this inactive splash a flat fill.
struct AccentPolicy
{
    DWORD state;
    DWORD flags;
    DWORD color;
    DWORD animation;
};

struct CompositionData
{
    int attribute;
    void* data;
    SIZE_T size;
};

constexpr DWORD accentDisabled = 0;
constexpr DWORD accentBlur = 3;
constexpr int accentAttribute = 19;

bool SetBlur(HWND window, bool enabled)
{
    using Setter = BOOL(WINAPI*)(HWND, CompositionData*);
    auto user32 = GetModuleHandleW(L"user32.dll");
    if (!user32)
    {
        return false;
    }
    auto set = reinterpret_cast<Setter>(GetProcAddress(user32, "SetWindowCompositionAttribute"));
    if (!set)
    {
        return false;
    }
    AccentPolicy policy{enabled ? accentBlur : accentDisabled, 0, 0xCCFFFFFF, 0};
    CompositionData data{accentAttribute, &policy, sizeof(policy)};
    return set(window, &data) != FALSE;
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
    Close();
}

void Splash::Show(Launch launch)
{
    if (window_)
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
    SetTimer(window_, showTimer, launch == Launch::Cold ? coldDelay : warmDelay, nullptr);
}

// The product window is drawn and waits to appear. A splash not yet on screen
// never appears. One on screen is seen for its minimum time before `show` lets
// the window appear, and stays until the window has appeared over it.
void Splash::Finish(std::function<void()> show)
{
    if (!shownAt_)
    {
        Close();
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
    if (buffered_)
    {
        BufferedPaintUnInit();
        buffered_ = false;
    }
    shownAt_ = 0;
    show_ = nullptr;
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
void Splash::SetTheme(std::string theme)
{
    if (theme == theme_)
    {
        return;
    }
    theme_ = std::move(theme);
    if (shownAt_)
    {
        ApplyTheme();
    }
}

void Splash::ApplyTheme()
{
    HIGHCONTRASTW contrast{sizeof(contrast)};
    SystemParametersInfoW(SPI_GETHIGHCONTRAST, sizeof(contrast), &contrast, 0);
    bool highContrast = (contrast.dwFlags & HCF_HIGHCONTRASTON) != 0;
    foreground_ = GetSysColor(COLOR_WINDOWTEXT);
    background_ = GetSysColor(COLOR_WINDOW);
    BOOL dark = !highContrast &&
        (theme_ == "dark" || (theme_ != "light" && !Preference(L"AppsUseLightTheme")));
    if (!highContrast)
    {
        foreground_ = dark ? RGB(255, 255, 255) : RGB(0, 0, 0);
        background_ = dark ? RGB(32, 32, 32) : RGB(243, 243, 243);
    }
    DwmSetWindowAttribute(window_, DWMWA_USE_IMMERSIVE_DARK_MODE, &dark, sizeof(dark));
    auto corners = DWMWCP_ROUND;
    DwmSetWindowAttribute(window_, DWMWA_WINDOW_CORNER_PREFERENCE, &corners, sizeof(corners));
    auto backdrop = DWMSBT_NONE;
    DwmSetWindowAttribute(window_, DWMWA_SYSTEMBACKDROP_TYPE, &backdrop, sizeof(backdrop));
    auto blur = !highContrast && buffered_ && Preference(L"EnableTransparency");
    acrylic_ = SetBlur(window_, blur) && blur;
    InvalidateRect(window_, nullptr, FALSE);
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
    PAINTSTRUCT paint{};
    auto device = BeginPaint(window, &paint);
    RECT bounds{};
    GetClientRect(window, &bounds);
    BP_PAINTPARAMS parameters{sizeof(parameters)};
    parameters.dwFlags = BPPF_ERASE;
    HDC bufferDevice = nullptr;
    auto buffer = BeginBufferedPaint(device, &bounds, BPBF_TOPDOWNDIB, &parameters, &bufferDevice);
    auto target = buffer ? bufferDevice : device;
    auto theme = OpenThemeData(window, L"CompositedWindow::Window");
    if (!acrylic_ || !buffer || !theme)
    {
        SetDCBrushColor(target, background_);
        FillRect(target, &bounds, static_cast<HBRUSH>(GetStockObject(DC_BRUSH)));
        if (buffer)
        {
            BufferedPaintSetAlpha(buffer, nullptr, 255);
        }
    }
    else
    {
        // The window surface holds premultiplied colour.
        auto scale = [](BYTE channel) { return static_cast<BYTE>(channel * tintOpacity / 255); };
        SetDCBrushColor(target, RGB(scale(GetRValue(background_)), scale(GetGValue(background_)),
            scale(GetBValue(background_))));
        FillRect(target, &bounds, static_cast<HBRUSH>(GetStockObject(DC_BRUSH)));
        BufferedPaintSetAlpha(buffer, nullptr, tintOpacity);
    }
    auto previous = SelectObject(target, font_);
    auto dpi = GetDpiForWindow(window);
    auto text = strings_.Text("startup", "opening");
    RECT measured{0, 0, bounds.right - Scale(2 * textInset, dpi), 0};
    DrawTextW(target, text.c_str(), -1, &measured, DT_CALCRECT | DT_WORDBREAK | DT_NOPREFIX);
    auto size = Scale(iconSize, dpi);
    auto gap = Scale(iconGap, dpi);
    auto top = std::max<LONG>(0, (bounds.bottom - size - gap - measured.bottom) / 2);
    auto icon = LoadImageW(GetModuleHandleW(nullptr), MAKEINTRESOURCEW(IDI_TINYTORRENT),
        IMAGE_ICON, size, size, LR_SHARED);
    DrawIconEx(target, (bounds.right - size) / 2, top, static_cast<HICON>(icon),
        size, size, 0, nullptr, DI_NORMAL);
    RECT textBounds{Scale(textInset, dpi), top + size + gap,
        bounds.right - Scale(textInset, dpi), bounds.bottom};
    if (theme && buffer)
    {
        DTTOPTS options{sizeof(options)};
        options.dwFlags = DTT_COMPOSITED | DTT_TEXTCOLOR;
        options.crText = foreground_;
        DrawThemeTextEx(theme, target, 0, 0, text.c_str(), -1,
            DT_CENTER | DT_WORDBREAK | DT_NOPREFIX, &textBounds, &options);
    }
    else
    {
        SetTextColor(target, foreground_);
        SetBkMode(target, TRANSPARENT);
        DrawTextW(target, text.c_str(), -1, &textBounds, DT_CENTER | DT_WORDBREAK | DT_NOPREFIX);
    }
    SelectObject(target, previous);
    if (theme)
    {
        CloseThemeData(theme);
    }
    if (buffer)
    {
        EndBufferedPaint(buffer, TRUE);
    }
    EndPaint(window, &paint);
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
        else if (first == showTimer)
        {
            KillTimer(window, showTimer);
            buffered_ = SUCCEEDED(BufferedPaintInit());
            ApplyTheme();
            Translate();
            shownAt_ = GetTickCount64();
            ShowWindow(window, SW_SHOW);
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
        ApplyTheme();
        return 0;
    case WM_NCDESTROY:
        window_ = nullptr;
        break;
    }
    return DefWindowProcW(window, message, first, second);
}
}
