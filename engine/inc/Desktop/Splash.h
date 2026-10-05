#pragma once
#include "Enums.h"
#include "Strings.h"
#include <windows.h>
#include <optional>

namespace tiny::desktop
{
// Shows that the product window is opening or, with two choices, why it could
// not open or exit. The owner decides what each choice does.
class Splash
{
public:
    using Choose = std::function<void(SplashFailure failure, SplashChoice choice)>;
    Splash(Strings const& strings, std::function<bool()> windowRunning, Choose choose);
    ~Splash();
    Splash(Splash const&) = delete;
    Splash& operator=(Splash const&) = delete;
    void Show(std::optional<SplashFailure> failure = {}, std::wstring detail = {});
    void Close();
    void Update();
    void Translate();
    // Handles the splash window's dialog keys. Returns true when it consumed
    // the message.
    bool PreTranslate(MSG& message);
private:
    static LRESULT CALLBACK Procedure(HWND window, UINT message, WPARAM first, LPARAM second);
    LRESULT Handle(HWND window, UINT message, WPARAM first, LPARAM second);
    void Create();
    void ApplyDpi(HWND window, UINT dpi, RECT const* bounds);
    void Arrange(HWND window);
    bool CanRetry() const;
    bool CanDismiss() const;
    Strings const& strings_;
    std::function<bool()> windowRunning_;
    Choose choose_;
    HWND window_ = nullptr;
    HFONT font_ = nullptr;
    std::optional<SplashFailure> failure_;
    std::wstring detail_;
};

char const* ToString(SplashFailure failure);
}
