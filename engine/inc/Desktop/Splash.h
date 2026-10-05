#pragma once
#include "Strings.h"
#include <windows.h>

namespace tt::desktop
{
class Splash
{
public:
    explicit Splash(Strings const& strings);
    ~Splash();
    Splash(Splash const&) = delete;
    Splash& operator=(Splash const&) = delete;
    void Show();
    void Finish();
    void Close();
    void Translate();
private:
    static LRESULT CALLBACK Procedure(HWND window, UINT message, WPARAM first, LPARAM second);
    LRESULT Handle(HWND window, UINT message, WPARAM first, LPARAM second);
    void ApplyTheme();
    void ApplyDpi(UINT dpi, RECT const* bounds);
    void Paint(HWND window);
    Strings const& strings_;
    HWND window_ = nullptr;
    HFONT font_ = nullptr;
    ULONGLONG shownAt_ = 0;
    bool buffered_ = false;
    bool acrylic_ = false;
    COLORREF foreground_ = RGB(0, 0, 0);
    COLORREF background_ = RGB(255, 255, 255);
};
}
