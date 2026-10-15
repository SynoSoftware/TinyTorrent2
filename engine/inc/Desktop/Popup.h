#pragma once
#include "Enums.h"
#include "Desktop/Acrylic.h"
#include <optional>
#include <string>
#include <vector>
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
    struct MenuItem
    {
        // A Lucide glyph, drawn when the app's Lucide font is installed beside
        // the engine.
        wchar_t glyph;
        std::wstring text;
        // The LPARAM sent when the person chooses the item.
        UINT id;
    };

    // A button under the message: a Lucide glyph before one word, as the
    // window's buttons are, with a tooltip for what the word does not say.
    struct Action
    {
        wchar_t glyph;
        std::wstring label;
        std::wstring tooltip;
        // The LPARAM sent when the person chooses the button.
        UINT id;
        // A button that only shows something, such as a folder, leaves the
        // notice open, so its message and other buttons stay available.
        bool closes = true;
    };

    struct Content
    {
        std::wstring title;
        std::wstring body;
        // Diagnostic lines stay on one line each, with paths shortened to fit.
        std::wstring detail;
        // Buttons share one row under the message in equal widths, as on
        // Windows' own notifications.
        std::vector<Action> actions;
        // The More menu, under its title and a separator. With any items, the
        // More button that opens it shows in the header while the pointer is
        // over the popup.
        std::wstring menuTitle;
        std::vector<MenuItem> menu;
        // Shown beside the product name in place of the product icon.
        HICON icon = nullptr;
        bool persistent = false;
    };

    // Selecting the body posts `selected` to `window` with PopupPart::Body as
    // its WPARAM; choosing a button posts PopupPart::Action, and an item of the
    // More menu sends PopupPart::More, each with its id as the LPARAM. Any of
    // them closes the popup, except a button that does not close it.
    Popup(HWND window, UINT selected);
    ~Popup();
    Popup(Popup const&) = delete;
    Popup& operator=(Popup const&) = delete;
    // Replaces the shown popup.
    void Show(Content content);
    void Close();
    void SetTheme(std::string theme);
    bool IsOpen() const { return window_ != nullptr; }
    bool IsPersistent() const { return window_ && content_.persistent; }
    bool IsMenuOpen() const { return menu_ != nullptr; }
private:
    class MenuAccessibility;
    static LRESULT CALLBACK Procedure(HWND window, UINT message, WPARAM first, LPARAM second);
    LRESULT Handle(HWND window, UINT message, WPARAM first, LPARAM second);
    static LRESULT CALLBACK MenuProcedure(HWND window, UINT message, WPARAM first, LPARAM second);
    LRESULT HandleMenu(HWND window, UINT message, WPARAM first, LPARAM second);
    void OpenMenu();
    void PaintMenu(HWND window);
    // Where the first menu item begins, below the title and separator.
    int MenuItemsTop(UINT dpi) const;
    // The width the icons and their gap take before the menu's text; none
    // without the Lucide font.
    int MenuIconsWidth(UINT dpi) const;
    std::optional<std::size_t> MenuItemAt(POINT point) const;
    RECT MenuBounds(std::size_t item) const;
    void HoverMenu(std::optional<std::size_t> item);
    void Choose(std::size_t item);
    void Layout();
    bool Slides() const;
    void StartSlide(bool leaving);
    void StopSlide();
    void Slide();
    // Starts the timer that closes the popup, unless it is persistent, under
    // the pointer, or under its open More menu.
    void ScheduleClose();
    void Paint(HWND window);
    void Hover(std::optional<PopupPart> part, std::optional<std::size_t> action);
    PopupPart Part(POINT point) const;
    std::optional<std::size_t> ActionAt(POINT point) const;
    // Gives each button its tooltip, over the bounds Layout gave it.
    void SetTooltips();
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
    HFONT icons_ = nullptr;
    // The app's Lucide font is installed beside the engine; a standalone
    // engine build draws the menu without icons.
    bool hasIcons_ = false;
    RECT header_{};
    RECT close_{};
    RECT title_{};
    RECT body_{};
    RECT detail_{};
    std::vector<RECT> actions_;
    RECT more_{};
    HWND tooltip_ = nullptr;
    // Where Layout places the popup, and when its current slide began; zero
    // when it is not sliding.
    POINT place_{};
    ULONGLONG slideStart_ = 0;
    // The current slide takes the popup away from its place.
    bool leaving_ = false;
    // The pointer is over the popup, which then stays open.
    bool tracking_ = false;
    HWND menu_ = nullptr;
    MenuAccessibility* accessibility_ = nullptr;
    // The height of one menu item, set when the menu opens.
    int menuRow_ = 0;
    std::optional<std::size_t> menuHover_;
    std::optional<PopupPart> hover_;
    std::optional<std::size_t> actionHover_;
};
}
