#include "Desktop/Acrylic.h"
#include "Resources.h"
#include <algorithm>
#include <cmath>
#include <dwmapi.h>

namespace tt::desktop
{
namespace
{
// The blur shows through a layer of the theme's background at these
// opacities, those of WinUI's AcrylicBackgroundFillColorDefaultBrush: its
// TintLuminosityOpacity is 0.85 in Light and 0.96 in Dark, in
// microsoft-ui-xaml's AcrylicBrush_themeresources.xaml. The layer then keeps
// the theme's colour over light or dark content behind the popup.
constexpr BYTE lightTint = 0xD9;
constexpr BYTE darkTint = 0xF5;
// Fluent's disabled text: the foreground at this opacity, out of 255.
constexpr BYTE disabledOpacity = 0x5C;

bool IsEnabled(wchar_t const* name)
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
// system backdrop left an inactive window a flat fill.
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

// `over` laid at `alpha`, from 0 to 1, on `under`, rounded to the nearest level.
BYTE Blend(BYTE over, BYTE under, double alpha)
{
    return static_cast<BYTE>(over * alpha + under * (1 - alpha) + 0.5);
}

// `over` laid at `opacity`, out of 255, on `under`.
COLORREF Mix(COLORREF over, COLORREF under, BYTE opacity)
{
    auto alpha = opacity / 255.0;
    return RGB(Blend(GetRValue(over), GetRValue(under), alpha), Blend(GetGValue(over), GetGValue(under), alpha),
        Blend(GetBValue(over), GetBValue(under), alpha));
}

// How much of the pixel at `x`, `y` lies inside `bounds` with corners of
// `radius` pixels, from 0 to 1.
double Coverage(RECT const& bounds, int radius, int x, int y)
{
    if (x < bounds.left || x >= bounds.right || y < bounds.top || y >= bounds.bottom)
    {
        return 0;
    }
    // How far the pixel's centre lies beyond the corner's arc.
    auto dx = std::max({0.0, bounds.left + radius - (x + 0.5), x + 0.5 - (bounds.right - radius)});
    auto dy = std::max({0.0, bounds.top + radius - (y + 0.5), y + 0.5 - (bounds.bottom - radius)});
    return dx > 0 && dy > 0 ? std::clamp(radius + 0.5 - std::hypot(dx, dy), 0.0, 1.0) : 1.0;
}

// Lays `color` over a premultiplied pixel at `alpha`, from 0 to 1.
void Blend(RGBQUAD& pixel, COLORREF color, double alpha)
{
    pixel.rgbRed = Blend(GetRValue(color), pixel.rgbRed, alpha);
    pixel.rgbGreen = Blend(GetGValue(color), pixel.rgbGreen, alpha);
    pixel.rgbBlue = Blend(GetBValue(color), pixel.rgbBlue, alpha);
    pixel.rgbReserved = Blend(255, pixel.rgbReserved, alpha);
}
}

int Scale(int value, UINT dpi)
{
    return MulDiv(value, dpi, USER_DEFAULT_SCREEN_DPI);
}

void Acrylic::Register(wchar_t const* name, WNDPROC procedure)
{
    WNDCLASSW type{};
    type.lpfnWndProc = procedure;
    type.hInstance = GetModuleHandleW(nullptr);
    type.hIcon = LoadIconW(type.hInstance, MAKEINTRESOURCEW(IDI_TINYTORRENT));
    type.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    type.lpszClassName = name;
    RegisterClassW(&type);
}

LOGFONTW Acrylic::MessageFont(UINT dpi)
{
    NONCLIENTMETRICSW metrics{sizeof(metrics)};
    SystemParametersInfoForDpi(SPI_GETNONCLIENTMETRICS, sizeof(metrics), &metrics, 0, dpi);
    return metrics.lfMessageFont;
}

Acrylic::Acrylic() : buffered_(SUCCEEDED(BufferedPaintInit())) {}

Acrylic::~Acrylic()
{
    if (buffered_)
    {
        BufferedPaintUnInit();
    }
}

void Acrylic::Apply(HWND window, Theme theme)
{
    HIGHCONTRASTW contrast{sizeof(contrast)};
    SystemParametersInfoW(SPI_GETHIGHCONTRAST, sizeof(contrast), &contrast, 0);
    bool highContrast = (contrast.dwFlags & HCF_HIGHCONTRASTON) != 0;
    foreground_ = GetSysColor(COLOR_WINDOWTEXT);
    background_ = GetSysColor(COLOR_WINDOW);
    BOOL dark = !highContrast &&
        (theme == Theme::Dark || (theme == Theme::System && !IsEnabled(L"AppsUseLightTheme")));
    if (highContrast)
    {
        disabled_ = GetSysColor(COLOR_GRAYTEXT);
        focus_ = GetSysColor(COLOR_HIGHLIGHT);
        edge_ = GetSysColor(COLOR_BTNTEXT);
    }
    else
    {
        foreground_ = dark ? RGB(255, 255, 255) : RGB(0, 0, 0);
        background_ = dark ? RGB(32, 32, 32) : RGB(243, 243, 243);
        tint_ = dark ? darkTint : lightTint;
        disabled_ = Mix(foreground_, background_, disabledOpacity);
        focus_ = foreground_;
        edge_.reset();
    }
    DwmSetWindowAttribute(window, DWMWA_USE_IMMERSIVE_DARK_MODE, &dark, sizeof(dark));
    auto corners = DWMWCP_ROUND;
    DwmSetWindowAttribute(window, DWMWA_WINDOW_CORNER_PREFERENCE, &corners, sizeof(corners));
    auto backdrop = DWMSBT_NONE;
    DwmSetWindowAttribute(window, DWMWA_SYSTEMBACKDROP_TYPE, &backdrop, sizeof(backdrop));
    auto blur = !highContrast && buffered_ && IsEnabled(L"EnableTransparency");
    blurred_ = SetBlur(window, blur) && blur;
    InvalidateRect(window, nullptr, FALSE);
}

std::optional<LRESULT> Acrylic::Handle(HWND window, UINT message, Theme theme)
{
    switch (message)
    {
    case WM_NCCALCSIZE:
    case WM_ERASEBKGND:
        return 0;
    case WM_NCHITTEST:
        return HTCLIENT;
    case WM_SETTINGCHANGE:
    case WM_SYSCOLORCHANGE:
    case WM_THEMECHANGED:
    case WM_DWMCOMPOSITIONCHANGED:
        Apply(window, theme);
        return 0;
    }
    return std::nullopt;
}

Acrylic::Canvas::Canvas(Acrylic const& acrylic, HWND window) : acrylic_(acrylic), window_(window)
{
    auto device = BeginPaint(window, &paint_);
    GetClientRect(window, &bounds_);
    BP_PAINTPARAMS parameters{sizeof(parameters)};
    parameters.dwFlags = BPPF_ERASE;
    HDC bufferDevice = nullptr;
    buffer_ = BeginBufferedPaint(device, &bounds_, BPBF_TOPDOWNDIB, &parameters, &bufferDevice);
    target_ = buffer_ ? bufferDevice : device;
    theme_ = OpenThemeData(window, L"CompositedWindow::Window");
    BYTE opacity = acrylic_.blurred_ && buffer_ && theme_ ? acrylic_.tint_ : 255;
    // The window surface holds premultiplied colour: the background laid over
    // transparent black.
    SetDCBrushColor(target_, Mix(acrylic_.background_, RGB(0, 0, 0), opacity));
    FillRect(target_, &bounds_, static_cast<HBRUSH>(GetStockObject(DC_BRUSH)));
    if (buffer_)
    {
        BufferedPaintSetAlpha(buffer_, nullptr, opacity);
    }
}

Acrylic::Canvas::~Canvas()
{
    if (theme_)
    {
        CloseThemeData(theme_);
    }
    if (buffer_)
    {
        EndBufferedPaint(buffer_, TRUE);
    }
    EndPaint(window_, &paint_);
}

// GDI fills leave the alpha of a 32-bit surface at zero and cannot round
// corners smoothly, so the fill writes the buffer's premultiplied pixels.
void Acrylic::Canvas::Fill(RECT const& bounds, COLORREF color, BYTE opacity, int radius)
{
    RGBQUAD* pixels = nullptr;
    int stride = 0;
    if (!buffer_ || FAILED(GetBufferedPaintBits(buffer_, &pixels, &stride)))
    {
        SetDCBrushColor(target_, Mix(color, acrylic_.background_, opacity));
        FillRect(target_, &bounds, static_cast<HBRUSH>(GetStockObject(DC_BRUSH)));
        return;
    }
    RECT area{};
    IntersectRect(&area, &bounds, &bounds_);
    for (auto y = area.top; y < area.bottom; ++y)
    {
        for (auto x = area.left; x < area.right; ++x)
        {
            Blend(pixels[y * stride + x], color, opacity * Coverage(bounds, radius, x, y) / 255);
        }
    }
}

void Acrylic::Canvas::Stroke(RECT const& bounds, COLORREF color, int width, int radius)
{
    auto inner = bounds;
    InflateRect(&inner, -width, -width);
    RGBQUAD* pixels = nullptr;
    int stride = 0;
    if (!buffer_ || FAILED(GetBufferedPaintBits(buffer_, &pixels, &stride)))
    {
        SetDCBrushColor(target_, color);
        auto brush = static_cast<HBRUSH>(GetStockObject(DC_BRUSH));
        for (RECT side : {RECT{bounds.left, bounds.top, bounds.right, inner.top},
            RECT{bounds.left, inner.bottom, bounds.right, bounds.bottom},
            RECT{bounds.left, inner.top, inner.left, inner.bottom},
            RECT{inner.right, inner.top, bounds.right, inner.bottom}})
        {
            FillRect(target_, &side, brush);
        }
        return;
    }
    RECT area{};
    IntersectRect(&area, &bounds, &bounds_);
    auto innerRadius = std::max(0, radius - width);
    for (auto y = area.top; y < area.bottom; ++y)
    {
        for (auto x = area.left; x < area.right; ++x)
        {
            auto coverage = Coverage(bounds, radius, x, y) - Coverage(inner, innerRadius, x, y);
            if (coverage > 0)
            {
                Blend(pixels[y * stride + x], color, coverage);
            }
        }
    }
}

void Acrylic::Canvas::Text(std::wstring const& text, RECT bounds, UINT format, HFONT font, COLORREF color)
{
    auto previous = SelectObject(target_, font);
    if (theme_ && buffer_)
    {
        DTTOPTS options{sizeof(options)};
        options.dwFlags = DTT_COMPOSITED | DTT_TEXTCOLOR;
        options.crText = color;
        DrawThemeTextEx(theme_, target_, 0, 0, text.c_str(), -1, format, &bounds, &options);
    }
    else
    {
        SetTextColor(target_, color);
        SetBkMode(target_, TRANSPARENT);
        DrawTextW(target_, text.c_str(), -1, &bounds, format);
    }
    SelectObject(target_, previous);
}

void Acrylic::Canvas::Icon(POINT at, int size, HICON icon)
{
    if (!icon)
    {
        icon = static_cast<HICON>(LoadImageW(GetModuleHandleW(nullptr), MAKEINTRESOURCEW(IDI_TINYTORRENT),
            IMAGE_ICON, size, size, LR_SHARED));
    }
    DrawIconEx(target_, at.x, at.y, icon, size, size, 0, nullptr, DI_NORMAL);
}
}
