#pragma once
#include "Enums.h"
#include "Strings.h"
#include "Desktop/Acrylic.h"
#include <deque>
#include <functional>
#include <string>
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
    void Show(Launch launch);
    void Record(ULONGLONG duration);
    void Finish(std::function<void()> show);
    void Close();
    void Translate();
    void SetTheme(Theme theme);
private:
    static LRESULT CALLBACK Procedure(HWND window, UINT message, WPARAM first, LPARAM second);
    LRESULT Handle(HWND window, UINT message, WPARAM first, LPARAM second);
    void ApplyDpi(UINT dpi, RECT const* bounds);
    void Paint(HWND window);
    Strings const& strings_;
    Acrylic acrylic_;
    HWND window_ = nullptr;
    HFONT font_ = nullptr;
    ULONGLONG shownAt_ = 0;
    std::function<void()> show_;
    std::deque<ULONGLONG> launches_;
    Theme theme_ = Theme::System;
};
}
