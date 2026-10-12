#pragma once
#include "Enums.h"
#include "Desktop/Acrylic.h"
#include <optional>
#include <string>
#include <windows.h>

namespace tt::desktop
{
// A notice that the engine draws above the notification area, where Windows
// shows its own. Windows lets a person turn off every app's notifications,
// balloons included, and a notice the person turned on in Settings must still
// appear.
class Popup
{
public:
    struct Content
    {
        std::wstring title;
        std::wstring body;
        // Diagnostic lines stay on one line each, with paths shortened to fit.
        std::wstring detail;
        // The popup has no button when this is empty.
        std::wstring button;
        // Shown beside the product name in place of the product icon.
        HICON icon = nullptr;
        bool persistent = false;
    };

    // Selecting the body or the button posts `selected` to `window`, with the
    // PopupPart as its WPARAM, and closes the popup.
    Popup(HWND window, UINT selected);
    ~Popup();
    Popup(Popup const&) = delete;
    Popup& operator=(Popup const&) = delete;
    // Replaces the shown popup.
    void Show(Content content);
    void Close();
    void SetTheme(std::string theme);
    bool IsPersistent() const { return window_ && content_.persistent; }
private:
    static LRESULT CALLBACK Procedure(HWND window, UINT message, WPARAM first, LPARAM second);
    LRESULT Handle(HWND window, UINT message, WPARAM first, LPARAM second);
    void Layout();
    bool Slides() const;
    void StartSlide(bool leaving);
    void StopSlide();
    void Slide();
    void Paint(HWND window);
    void Hover(std::optional<PopupPart> part);
    PopupPart Part(POINT point) const;
    void DeleteFonts();
    HWND owner_;
    UINT selected_;
    Acrylic acrylic_;
    std::string theme_ = "system";
    HWND window_ = nullptr;
    Content content_;
    HFONT font_ = nullptr;
    HFONT caption_ = nullptr;
    HFONT headline_ = nullptr;
    HFONT glyphs_ = nullptr;
    RECT header_{};
    RECT close_{};
    RECT title_{};
    RECT body_{};
    RECT detail_{};
    RECT button_{};
    // Where Layout places the popup, and when its current slide began; zero
    // when it is not sliding.
    POINT place_{};
    ULONGLONG slideStart_ = 0;
    // The current slide takes the popup away from its place.
    bool leaving_ = false;
    // The pointer is over the popup, which then stays open.
    bool tracking_ = false;
    std::optional<PopupPart> hover_;
};
}
