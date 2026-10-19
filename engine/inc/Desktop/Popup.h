#pragma once
#include "Enums.h"
#include "Strings.h"
#include "Desktop/Acrylic.h"
#include <filesystem>
#include <functional>
#include <optional>
#include <string>
#include <vector>
#include <windows.h>

namespace tt::desktop
{
// A toast or a prompt that the engine draws above the notification area,
// where Windows shows its own notifications. Windows lets a person turn off
// every app's notifications, balloons included, and a toast the person turned
// on in Settings must still appear.
class Popup
{
public:
    struct MenuItem
    {
        // A Lucide glyph, drawn when the app's Lucide font is installed beside
        // the engine.
        wchar_t glyph;
        std::wstring text;
        std::function<void()> run;
        std::wstring automationId;
    };

    // A button under the message: a Lucide glyph before a short label, as the
    // window's buttons are, with a tooltip for what the label does not say.
    struct Action
    {
        wchar_t glyph;
        std::wstring label;
        std::wstring tooltip;
        std::function<void()> run;
        // A button that only shows something, such as a folder, or that
        // waits for what it started, as a prompt's Exit does, leaves the popup
        // open, so its message and other buttons stay available.
        bool closes = true;
        // A disabled button looks unavailable, takes no hover, focus or click,
        // and accessibility reports it unavailable.
        bool enabled = true;
        std::wstring automationId;
    };

    // A program or folder path the person may need to identify: one line
    // shortened in its middle, the whole path in its tooltip, and Copy path on
    // the context menu and Ctrl+C.
    struct Location
    {
        std::wstring caption;
        std::filesystem::path path;
    };

    struct Content
    {
        std::wstring title;
        std::wstring body;
        // Runs when the person selects a toast's message; without it,
        // selecting the message does nothing, because only Close and Dismiss
        // close a toast before its time.
        std::function<void()> select;
        std::optional<Location> location;
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
        // Every toast ends its buttons with Dismiss, which the popup adds. An
        // interactive one takes the focus as a prompt does, without a taskbar
        // button, so Tab, Enter and Escape reach its buttons; Enter runs
        // Dismiss until another button has the focus.
        ToastStay stay = ToastStay::Timed;
    };

    // A choice the person must make before the engine continues. A prompt
    // never closes by itself, and Escape, Alt+F4 and its close button cancel
    // it.
    struct Prompt
    {
        std::wstring title;
        std::wstring body;
        std::optional<Location> location;
        // The default answer, which Enter runs while no button has focus.
        Action answer;
        // Drawn after the answer, left to right.
        std::vector<Action> others;
    };

    // `strings` names the popup's own parts, read when they show, so a
    // language change applies at once. The caller keeps it alive.
    // Selecting a button or a More menu item closes the popup, except a button
    // that does not close it, and then runs what the content gave that part,
    // as selecting a toast's message does when the content acts on it. That
    // code may show new content.
    explicit Popup(Strings const& strings);
    ~Popup();
    Popup(Popup const&) = delete;
    Popup& operator=(Popup const&) = delete;
    // Replaces the shown popup with a toast. Only an interactive toast takes
    // the focus.
    void Show(Content toast);
    // The first Ask shows the prompt, requests the foreground and focuses the
    // answer when it is enabled. While the prompt is open, asking again
    // changes its text and buttons in place, without requesting the
    // foreground. The focused button keeps focus while it stays enabled.
    // Without a focused button, the answer takes the focus as soon as it is
    // enabled, so the focus ring shows where Enter goes.
    void Ask(Prompt prompt);
    void Close();
    void SetTheme(Theme theme);
    bool IsOpen() const { return window_ != nullptr; }
    bool IsPersistent() const { return window_ && content_.stay != ToastStay::Timed; }
    bool IsMenuOpen() const { return menu_ != nullptr; }
private:
    class Accessibility;
    // A part the pointer or a key can press; `index` is the button's when the
    // part is one.
    struct Target
    {
        PopupPart part;
        std::size_t index = 0;
        bool operator==(Target const&) const = default;
    };
    // What accessibility reports for one part of a popup window; the first
    // element of a window is the window itself.
    struct Element
    {
        std::wstring name;
        std::wstring description;
        std::wstring automationId;
        long role = 0;
        long state = 0;
        // Empty when the part has none.
        std::wstring defaultAction;
        // In client coordinates.
        RECT bounds{};
    };
    // A slide that began at `start`, and takes the popup away from its place
    // when `leaving`.
    struct Sliding
    {
        ULONGLONG start;
        bool leaving;
    };
    static LRESULT CALLBACK Procedure(HWND window, UINT message, WPARAM first, LPARAM second);
    LRESULT Handle(HWND window, UINT message, WPARAM first, LPARAM second);
    static LRESULT CALLBACK MenuProcedure(HWND window, UINT message, WPARAM first, LPARAM second);
    LRESULT HandleMenu(HWND window, UINT message, WPARAM first, LPARAM second);
    // The keys of a popup that has the focus; false for a key left to
    // DefWindowProc, such as the Menu key, whose release opens the context
    // menu.
    bool HandleKey(UINT message, WPARAM key, LPARAM flags);
    // Creates the window, with the styles of a prompt, an interactive toast
    // or a toast.
    void Create();
    // An open prompt or interactive toast, which takes the focus and keys.
    bool Interactive() const;
    // The button Enter runs while no button has the focus: a prompt's answer,
    // or an interactive toast's Dismiss.
    std::size_t Answer() const;
    // Shows the current content, creating the window when there is none.
    void Present();
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
    // Starts the timer that closes the popup, unless it stays until dismissed,
    // is under the pointer, or is under its open More menu.
    void ScheduleClose();
    void Paint(HWND window);
    void Hover(std::optional<Target> target);
    // Nothing outside the client area, so a press dragged off the popup and
    // released elsewhere selects nothing.
    std::optional<Target> PartAt(POINT point) const;
    // Gives each button its tooltip, over the bounds Layout gave it.
    void SetTooltips();
    // The lines under the message: the location's caption and path, then the
    // diagnostic detail.
    std::vector<std::wstring> Lines() const;
    // The parts accessibility lists, in child ID order after the window.
    std::vector<Target> Targets() const;
    std::vector<Element> Elements(HWND window) const;
    // The child ID with keyboard focus, when `window` has the focus.
    std::optional<long> FocusedChild(HWND window) const;
    bool Select(HWND window, long child, long flags);
    LRESULT Serve(Accessibility*& accessibility, HWND window, WPARAM first);
    // Gives the focus to a button, or to none. A disabled button never takes
    // the focus, so Focus refuses it, changes nothing and returns false.
    bool Focus(std::optional<std::size_t> action);
    // What a click on `target`, or its accessible default action, does.
    void Run(Target target);
    // The only path that runs a button, and the one that refuses a disabled
    // one.
    void Invoke(std::size_t action);
    void CopyPath();
    void DeleteFonts();
    Strings const& strings_;
    Acrylic acrylic_;
    Theme theme_ = Theme::System;
    HWND window_ = nullptr;
    Content content_;
    // The popup asks a prompt, which has a taskbar button and stays below the
    // windows the person turns to.
    bool asking_ = false;
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
    Accessibility* accessibility_ = nullptr;
    // Where Layout places the popup.
    POINT place_{};
    std::optional<Sliding> sliding_;
    // The pointer is over the popup, which then stays open.
    bool tracking_ = false;
    HWND menu_ = nullptr;
    Accessibility* menuAccessibility_ = nullptr;
    // The height of one menu item, set when the menu opens.
    int menuRow_ = 0;
    std::optional<std::size_t> menuHover_;
    std::optional<Target> hover_;
    // What a mouse press or a Space press holds down until its release.
    std::optional<Target> pressed_;
    // The button with keyboard focus while the prompt has the focus.
    std::optional<std::size_t> focused_;
};
}
