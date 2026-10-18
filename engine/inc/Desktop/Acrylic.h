#pragma once
#include <optional>
#include <string>
#include <windows.h>
#include <uxtheme.h>

namespace tt::desktop
{
// The translucent surface of the engine's own windows, the splash and the
// notice popup: a blur behind a tint of the theme's background, or a solid
// background when high contrast or Windows' transparency setting rules the
// blur out.
class Acrylic
{
public:
    Acrylic();
    ~Acrylic();
    Acrylic(Acrylic const&) = delete;
    Acrylic& operator=(Acrylic const&) = delete;
    // Follows `theme`, the app's "light", "dark" or "system" choice, and
    // Windows' high contrast and transparency settings.
    void Apply(HWND window, std::string const& theme);
    COLORREF Foreground() const { return foreground_; }
    COLORREF Disabled() const { return disabled_; }
    // The keyboard focus ring.
    COLORREF Focus() const { return focus_; }
    // High contrast outlines each button, because its faint fill can match the
    // background there; otherwise a button has no edge.
    std::optional<COLORREF> Edge() const { return edge_; }

    // One WM_PAINT pass over the window. It fills the backdrop when created
    // and shows what was drawn when destroyed.
    class Canvas
    {
    public:
        Canvas(Acrylic const& acrylic, HWND window);
        ~Canvas();
        Canvas(Canvas const&) = delete;
        Canvas& operator=(Canvas const&) = delete;
        RECT const& Bounds() const { return bounds_; }
        HDC Device() const { return target_; }
        // Lays `color` at `opacity`, out of 255, over what is drawn inside
        // `bounds`, with corners of `radius` pixels.
        void Fill(RECT const& bounds, COLORREF color, BYTE opacity, int radius);
        // Draws an outline `width` pixels wide just inside `bounds`, with
        // outer corners of `radius` pixels.
        void Stroke(RECT const& bounds, COLORREF color, int width, int radius);
        void Text(std::wstring const& text, RECT bounds, UINT format, HFONT font, COLORREF color);
    private:
        Acrylic const& acrylic_;
        HWND window_;
        PAINTSTRUCT paint_{};
        RECT bounds_{};
        HDC target_ = nullptr;
        HPAINTBUFFER buffer_ = nullptr;
        HTHEME theme_ = nullptr;
    };

private:
    bool buffered_;
    bool blurred_ = false;
    COLORREF foreground_ = RGB(0, 0, 0);
    COLORREF background_ = RGB(255, 255, 255);
    // How much of the background covers the blur, out of 255.
    BYTE tint_ = 255;
    COLORREF disabled_ = RGB(128, 128, 128);
    COLORREF focus_ = RGB(0, 0, 0);
    std::optional<COLORREF> edge_;
};
}
