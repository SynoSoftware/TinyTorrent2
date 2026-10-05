#include "Desktop/Splash.h"
#include "Resources.h"
#include <algorithm>

namespace tt::desktop
{
namespace
{
constexpr wchar_t windowClass[] = L"TinyTorrent.Startup";

// The buttons take the standard dialog IDs, so IsDialogMessage sends Enter to
// the first and Escape to the second.
constexpr int firstId = IDOK;
constexpr int secondId = IDCANCEL;
constexpr int textId = secondId + 1;
constexpr int iconId = textId + 1;

// Layout in pixels at 96 DPI.
constexpr SIZE openingSize{350, 170};
constexpr SIZE failureSize{440, 240};
constexpr int buttonWidth = 82;
constexpr int buttonHeight = 28;
constexpr int buttonGap = 10;
constexpr int buttonRight = 20;
constexpr int buttonBottom = 14;
constexpr int buttonArea = 46;
constexpr int textInset = 16;
constexpr int textTop = 12;
constexpr int iconSize = 48;
constexpr int iconGap = 12;

int Scale(int value, UINT dpi)
{
    return MulDiv(value, dpi, USER_DEFAULT_SCREEN_DPI);
}

// The choices on the first and second buttons.
struct Choices
{
    SplashChoice first;
    SplashChoice second;
};

Choices Offered(SplashFailure failure)
{
    switch (failure)
    {
    case SplashFailure::Launch:
    case SplashFailure::Startup:
        return {SplashChoice::Retry, SplashChoice::Close};
    case SplashFailure::Save:
        return {SplashChoice::Retry, SplashChoice::ExitAnyway};
    case SplashFailure::FilesBusy:
        return {SplashChoice::Wait, SplashChoice::Cancel};
    case SplashFailure::Unresponsive:
        return {SplashChoice::ExitAnyway, SplashChoice::Cancel};
    }
    return {SplashChoice::Retry, SplashChoice::Close};
}

char const* ToString(SplashChoice choice)
{
    switch (choice)
    {
    case SplashChoice::Retry: return "retry";
    case SplashChoice::Close: return "close";
    case SplashChoice::Cancel: return "cancel";
    case SplashChoice::ExitAnyway: return "exit_anyway";
    case SplashChoice::Wait: return "wait";
    }
    return "";
}
}

char const* ToString(SplashFailure failure)
{
    switch (failure)
    {
    case SplashFailure::Launch: return "launch";
    case SplashFailure::Startup: return "startup";
    case SplashFailure::Save: return "save";
    case SplashFailure::FilesBusy: return "files_busy";
    case SplashFailure::Unresponsive: return "unresponsive";
    }
    return "";
}

Splash::Splash(Strings const& strings, std::function<bool()> windowRunning, Choose choose)
    : strings_(strings), windowRunning_(std::move(windowRunning)), choose_(std::move(choose))
{
    auto module = GetModuleHandleW(nullptr);
    WNDCLASSW type{};
    type.lpfnWndProc = Procedure;
    type.hInstance = module;
    type.hIcon = LoadIconW(module, MAKEINTRESOURCEW(IDI_TINYTORRENT));
    type.hbrBackground = GetSysColorBrush(COLOR_WINDOW);
    type.lpszClassName = windowClass;
    RegisterClassW(&type);
}

Splash::~Splash()
{
    Close();
    if (font_)
    {
        DeleteObject(font_);
    }
}

// Retry for a window that failed to open starts a new window process, so it
// waits until the previous one has exited. Every other first choice is
// always available.
bool Splash::CanChooseFirst() const
{
    auto opening = failure_ == SplashFailure::Launch || failure_ == SplashFailure::Startup;
    return !opening || !windowRunning_();
}

// The window's Close and Escape choose the second button, except Exit anyway,
// which can lose changes, so only a click on its button chooses it.
bool Splash::CanDismiss() const
{
    return failure_ && Offered(*failure_).second != SplashChoice::ExitAnyway;
}

bool Splash::PreTranslate(MSG& message)
{
    if (!window_)
    {
        return false;
    }
    // IsDialogMessage turns Escape into a click on the Close button.
    bool escape = message.message == WM_KEYDOWN && message.wParam == VK_ESCAPE &&
        (message.hwnd == window_ || IsChild(window_, message.hwnd));
    if (escape && !CanDismiss())
    {
        return true;
    }
    return IsDialogMessageW(window_, &message);
}

void Splash::Show(std::optional<SplashFailure> failure, std::wstring detail)
{
    failure_ = failure;
    detail_ = std::move(detail);
    if (!window_)
    {
        Create();
    }
    auto firstButton = GetDlgItem(window_, firstId);
    auto secondButton = GetDlgItem(window_, secondId);
    if (failure_)
    {
        auto choices = Offered(*failure_);
        SetWindowTextW(firstButton, strings_.Text("dialog", ToString(choices.first)).c_str());
        SetWindowTextW(secondButton, strings_.Text("dialog", ToString(choices.second)).c_str());
    }
    ShowWindow(firstButton, failure_ ? SW_SHOW : SW_HIDE);
    ShowWindow(secondButton, failure_ ? SW_SHOW : SW_HIDE);
    ShowWindow(GetDlgItem(window_, iconId), failure_ ? SW_HIDE : SW_SHOW);
    EnableWindow(firstButton, CanChooseFirst());
    auto text = failure_ ? strings_.Text("error", ToString(*failure_)) : strings_.Text("startup", "opening");
    if (!detail_.empty())
    {
        text += L"\n" + detail_;
    }
    SetWindowTextW(GetDlgItem(window_, textId), text.c_str());
    auto dpi = GetDpiForWindow(window_);
    auto size = failure_ ? failureSize : openingSize;
    SetWindowPos(window_, nullptr, 0, 0, Scale(size.cx, dpi), Scale(size.cy, dpi),
        SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
    SendMessageW(window_, WM_SIZE, 0, 0);
    InvalidateRect(window_, nullptr, TRUE);
    if (failure_)
    {
        SetForegroundWindow(window_);
    }
}

void Splash::Create()
{
    auto module = GetModuleHandleW(nullptr);
    POINT point{};
    GetCursorPos(&point);
    MONITORINFO monitor{sizeof(monitor)};
    GetMonitorInfoW(MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST), &monitor);
    window_ = CreateWindowExW(WS_EX_TOOLWINDOW, windowClass, productName, WS_POPUP | WS_BORDER,
        point.x, point.y, openingSize.cx, openingSize.cy, nullptr, nullptr, module, this);
    auto dpi = GetDpiForWindow(window_);
    auto width = Scale(openingSize.cx, dpi);
    auto height = Scale(openingSize.cy, dpi);
    auto const& work = monitor.rcWork;
    SetWindowPos(window_, HWND_TOP, work.left + (work.right - work.left - width) / 2,
        work.top + (work.bottom - work.top - height) / 2, width, height, SWP_NOACTIVATE);
    auto control = [&](wchar_t const* type, DWORD style, int id)
    {
        auto menu = reinterpret_cast<HMENU>(static_cast<INT_PTR>(id));
        CreateWindowW(type, L"", WS_CHILD | style, 0, 0, 0, 0, window_, menu, module, nullptr);
    };
    control(L"BUTTON", WS_TABSTOP | BS_DEFPUSHBUTTON, firstId);
    control(L"BUTTON", WS_TABSTOP | BS_PUSHBUTTON, secondId);
    control(L"STATIC", WS_VISIBLE | SS_CENTER | SS_NOPREFIX, textId);
    control(L"STATIC", SS_ICON | SS_CENTERIMAGE, iconId);
    SendMessageW(window_, WM_DPICHANGED, dpi, 0);
    ShowWindow(window_, SW_SHOWNOACTIVATE);
}

void Splash::Close()
{
    if (window_)
    {
        DestroyWindow(window_);
    }
}

void Splash::Update()
{
    if (window_ && failure_)
    {
        EnableWindow(GetDlgItem(window_, firstId), CanChooseFirst());
    }
}

void Splash::Translate()
{
    if (window_)
    {
        Show(failure_, detail_);
    }
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
    if (!owner)
    {
        return DefWindowProcW(window, message, first, second);
    }
    return owner->Handle(window, message, first, second);
}

LRESULT Splash::Handle(HWND window, UINT message, WPARAM first, LPARAM second)
{
    if (message == WM_NCDESTROY)
    {
        window_ = nullptr;
    }
    if (message == WM_DPICHANGED)
    {
        auto dpi = first ? LOWORD(first) : GetDpiForWindow(window);
        ApplyDpi(window, dpi, reinterpret_cast<RECT const*>(second));
        return 0;
    }
    if (message == WM_SIZE)
    {
        Arrange(window);
        return 0;
    }
    if (message == WM_COMMAND && (LOWORD(first) == firstId || LOWORD(first) == secondId))
    {
        auto button = LOWORD(first);
        if (!failure_ || !IsWindowEnabled(GetDlgItem(window, button)))
        {
            return 0;
        }
        auto failure = *failure_;
        auto choices = Offered(failure);
        Close();
        choose_(failure, button == firstId ? choices.first : choices.second);
        return 0;
    }
    if (message == WM_CLOSE)
    {
        if (CanDismiss())
        {
            SendMessageW(window, WM_COMMAND, secondId, 0);
        }
        return 0;
    }
    if (message == WM_CTLCOLORSTATIC)
    {
        auto device = reinterpret_cast<HDC>(first);
        SetTextColor(device, GetSysColor(COLOR_WINDOWTEXT));
        SetBkColor(device, GetSysColor(COLOR_WINDOW));
        return reinterpret_cast<LRESULT>(GetSysColorBrush(COLOR_WINDOW));
    }
    if (message == WM_SETTINGCHANGE || message == WM_SYSCOLORCHANGE)
    {
        InvalidateRect(window, nullptr, TRUE);
    }
    return DefWindowProcW(window, message, first, second);
}

// Recreates the font and icon for the new DPI. `bounds` is the size Windows
// suggests, or null when the window is new.
void Splash::ApplyDpi(HWND window, UINT dpi, RECT const* bounds)
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
        SetWindowPos(window, nullptr, bounds->left, bounds->top, bounds->right - bounds->left,
            bounds->bottom - bounds->top, SWP_NOZORDER | SWP_NOACTIVATE);
    }
    for (int id : {firstId, secondId, textId})
    {
        SendMessageW(GetDlgItem(window, id), WM_SETFONT, reinterpret_cast<WPARAM>(font_), TRUE);
    }
    auto icon = LoadImageW(GetModuleHandleW(nullptr), MAKEINTRESOURCEW(IDI_TINYTORRENT), IMAGE_ICON,
        Scale(iconSize, dpi), Scale(iconSize, dpi), LR_SHARED);
    SendMessageW(GetDlgItem(window, iconId), STM_SETICON, reinterpret_cast<WPARAM>(icon), 0);
    SendMessageW(window, WM_SIZE, 0, 0);
}

// Places the buttons in the bottom-right corner and centres the text, with the
// icon above it while the window is opening.
void Splash::Arrange(HWND window)
{
    RECT bounds{};
    GetClientRect(window, &bounds);
    auto dpi = GetDpiForWindow(window);
    auto top = bounds.bottom - Scale(buttonBottom + buttonHeight, dpi);
    auto firstLeft = bounds.right - Scale(buttonRight + 2 * buttonWidth + buttonGap, dpi);
    auto secondLeft = bounds.right - Scale(buttonRight + buttonWidth, dpi);
    SIZE button{Scale(buttonWidth, dpi), Scale(buttonHeight, dpi)};
    SetWindowPos(GetDlgItem(window, firstId), nullptr, firstLeft, top, button.cx, button.cy, SWP_NOZORDER);
    SetWindowPos(GetDlgItem(window, secondId), nullptr, secondLeft, top, button.cx, button.cy, SWP_NOZORDER);
    auto status = GetDlgItem(window, textId);
    if (!status)
    {
        return;
    }
    std::wstring text(GetWindowTextLengthW(status) + 1, L'\0');
    GetWindowTextW(status, text.data(), static_cast<int>(text.size()));
    auto width = bounds.right - Scale(2 * textInset, dpi);
    RECT measured{0, 0, width, 0};
    auto device = GetDC(window);
    auto font = SelectObject(device, font_);
    DrawTextW(device, text.c_str(), -1, &measured, DT_CALCRECT | DT_WORDBREAK | DT_NOPREFIX);
    SelectObject(device, font);
    ReleaseDC(window, device);
    auto height = bounds.bottom - (failure_ ? Scale(buttonArea, dpi) : 0);
    auto icon = failure_ ? 0 : Scale(iconSize + iconGap, dpi);
    auto y = std::max<LONG>(Scale(textTop, dpi), (height - measured.bottom - icon) / 2);
    if (icon)
    {
        SetWindowPos(GetDlgItem(window, iconId), nullptr, (bounds.right - Scale(iconSize, dpi)) / 2, y,
            Scale(iconSize, dpi), Scale(iconSize, dpi), SWP_NOZORDER);
    }
    y += icon;
    auto textHeight = std::min<LONG>(measured.bottom, height - y);
    SetWindowPos(status, nullptr, Scale(textInset, dpi), y, width, textHeight, SWP_NOZORDER);
}
}
