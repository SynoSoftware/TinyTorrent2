#include "Desktop/Acrylic.h"
#include <algorithm>
#include <cmath>
#include <dwmapi.h>

namespace tt::desktop
{
namespace
{
// The blur shows through a layer of the theme's background at this opacity, so
// the text keeps its contrast in either theme.
constexpr BYTE tintOpacity = 0x99;

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

// The window surface holds premultiplied colour.
BYTE Premultiply(BYTE channel, BYTE opacity)
{
    return static_cast<BYTE>(channel * opacity / 255);
}
}

Acrylic::Acrylic() : buffered_(SUCCEEDED(BufferedPaintInit())) {}

Acrylic::~Acrylic()
{
    if (buffered_)
    {
        BufferedPaintUnInit();
    }
}

void Acrylic::Apply(HWND window, std::string const& theme)
{
    HIGHCONTRASTW contrast{sizeof(contrast)};
    SystemParametersInfoW(SPI_GETHIGHCONTRAST, sizeof(contrast), &contrast, 0);
    bool highContrast = (contrast.dwFlags & HCF_HIGHCONTRASTON) != 0;
    foreground_ = GetSysColor(COLOR_WINDOWTEXT);
    background_ = GetSysColor(COLOR_WINDOW);
    BOOL dark = !highContrast &&
        (theme == "dark" || (theme != "light" && !IsEnabled(L"AppsUseLightTheme")));
    if (!highContrast)
    {
        foreground_ = dark ? RGB(255, 255, 255) : RGB(0, 0, 0);
        background_ = dark ? RGB(32, 32, 32) : RGB(243, 243, 243);
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
    BYTE opacity = acrylic_.blurred_ && buffer_ && theme_ ? tintOpacity : 255;
    auto const& background = acrylic_.background_;
    SetDCBrushColor(target_, RGB(Premultiply(GetRValue(background), opacity),
        Premultiply(GetGValue(background), opacity), Premultiply(GetBValue(background), opacity)));
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
        auto blend = [opacity](BYTE over, BYTE under)
        {
            return static_cast<BYTE>(Premultiply(over, opacity) + Premultiply(under, 255 - opacity));
        };
        auto const& background = acrylic_.background_;
        SetDCBrushColor(target_, RGB(blend(GetRValue(color), GetRValue(background)),
            blend(GetGValue(color), GetGValue(background)), blend(GetBValue(color), GetBValue(background))));
        FillRect(target_, &bounds, static_cast<HBRUSH>(GetStockObject(DC_BRUSH)));
        return;
    }
    RECT area{};
    IntersectRect(&area, &bounds, &bounds_);
    for (auto y = area.top; y < area.bottom; ++y)
    {
        for (auto x = area.left; x < area.right; ++x)
        {
            // How far the pixel's centre lies beyond the corner's arc.
            auto dx = std::max({0.0, bounds.left + radius - (x + 0.5), x + 0.5 - (bounds.right - radius)});
            auto dy = std::max({0.0, bounds.top + radius - (y + 0.5), y + 0.5 - (bounds.bottom - radius)});
            auto coverage = dx > 0 && dy > 0 ? std::clamp(radius + 0.5 - std::hypot(dx, dy), 0.0, 1.0) : 1.0;
            auto alpha = opacity * coverage / 255;
            auto& pixel = pixels[y * stride + x];
            auto mix = [alpha](BYTE over, BYTE under)
            {
                return static_cast<BYTE>(over * alpha + under * (1 - alpha) + 0.5);
            };
            pixel.rgbRed = mix(GetRValue(color), pixel.rgbRed);
            pixel.rgbGreen = mix(GetGValue(color), pixel.rgbGreen);
            pixel.rgbBlue = mix(GetBValue(color), pixel.rgbBlue);
            pixel.rgbReserved = mix(255, pixel.rgbReserved);
        }
    }
}

void Acrylic::Canvas::Text(std::wstring const& text, RECT bounds, UINT format, HFONT font)
{
    auto previous = SelectObject(target_, font);
    if (theme_ && buffer_)
    {
        DTTOPTS options{sizeof(options)};
        options.dwFlags = DTT_COMPOSITED | DTT_TEXTCOLOR;
        options.crText = acrylic_.foreground_;
        DrawThemeTextEx(theme_, target_, 0, 0, text.c_str(), -1, format, &bounds, &options);
    }
    else
    {
        SetTextColor(target_, acrylic_.foreground_);
        SetBkMode(target_, TRANSPARENT);
        DrawTextW(target_, text.c_str(), -1, &bounds, format);
    }
    SelectObject(target_, previous);
}
}
