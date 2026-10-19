#include "Desktop/Popup.h"
#include "Engine.h"
#include "Resources.h"
#include <algorithm>
#include <cmath>
#include <filesystem>
#include <sstream>
#include <utility>
#include <oleacc.h>
#include <UIAutomation.h>
#include <servprov.h>
#include <windowsx.h>
#include <commctrl.h>
#include <oleauto.h>
#include <wrl.h>

namespace tt::desktop
{
namespace
{
constexpr wchar_t windowClass[] = L"TinyTorrent.Notice";
constexpr wchar_t menuClass[] = L"TinyTorrent.NoticeMenu";
constexpr UINT_PTR closeTimer = 1;
constexpr UINT_PTR slideTimer = 2;
// The slide in from the screen's edge and back out, as Windows notifications
// arrive and leave.
constexpr ULONGLONG slideDuration = 250;
constexpr UINT slideInterval = 10;
// Seconds, when Windows does not say how long notifications stay.
constexpr ULONG defaultDuration = 5;
// Segoe MDL2 Assets' ChromeClose, which Windows 10 and 11 both have.
constexpr wchar_t closeGlyph[] = L"\xE8BB";
// Segoe MDL2 Assets' More, the button Windows' own notifications show.
constexpr wchar_t moreGlyph[] = L"\xE712";
// Lucide's x glyph, the Dismiss button's icon.
constexpr wchar_t dismissGlyph = L'\uE1B2';
constexpr UINT textFormat = DT_WORDBREAK | DT_EDITCONTROL | DT_NOPREFIX | DT_END_ELLIPSIS;
constexpr UINT labelFormat = DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX | DT_END_ELLIPSIS;
constexpr UINT detailFormat = DT_SINGLELINE | DT_NOPREFIX;
// A WM_KEYDOWN and WM_SYSKEYDOWN flag: the key was already down, because it
// repeats or was held before the popup had the focus.
constexpr LPARAM keyHeld = 1 << 30;

// Layout in effective pixels.
constexpr int popupWidth = 360;
constexpr int padding = 16;
constexpr int margin = 12;
constexpr int iconSize = 16;
constexpr int iconGap = 8;
constexpr int titleGap = 12;
constexpr int bodyGap = 8;
constexpr int detailGap = 12;
constexpr int actionGap = 16;
constexpr int actionHeight = 32;
constexpr int actionSpacing = 8;
constexpr int actionInset = 12;
// The window's buttons: a 16-pixel icon, 8 pixels before the word.
constexpr int actionIconSize = 16;
constexpr int actionIconGap = 8;
// Fluent's focus visual: a 2-pixel ring a pixel outside the button.
constexpr int focusWidth = 2;
constexpr int focusGap = 1;
constexpr int edgeWidth = 1;
constexpr int lineGap = 4;
constexpr int fontSize = 14;
constexpr int captionSize = 12;
constexpr int closeSize = 32;
constexpr int closeInset = 8;
constexpr int glyphSize = 10;
constexpr int radius = 4;
// The More menu, sized as Windows' own notification menu.
constexpr int menuPadding = 4;
constexpr int menuRow = 36;
constexpr int menuInset = 12;
constexpr int menuIconSize = 16;
constexpr int menuIconGap = 12;
constexpr int menuGap = 4;
// The separator under the menu's title: a one-pixel line centred in it.
constexpr int menuSeparator = 9;
// Bound long names and diagnostics so a burst cannot fill the work area.
constexpr int textLimit = 160;

// The fill of the foreground colour over the backdrop under the pointer, out of 255.
constexpr BYTE hoverOpacity = 0x1A;
// The fill of a pressed button, below its rest fill as Fluent's pressed
// state is, out of 255.
constexpr BYTE pressedOpacity = 0x08;
// The fill of a button at rest, Fluent's subtle control fill, out of 255.
constexpr BYTE buttonOpacity = 0x0F;
// Fluent's divider stroke, out of 255.
constexpr BYTE dividerOpacity = 0x14;

int Scale(int value, UINT dpi)
{
    return MulDiv(value, dpi, USER_DEFAULT_SCREEN_DPI);
}

// The monitor where Windows shows its notifications: the taskbar's.
MONITORINFO Taskbar()
{
    MONITORINFO monitor{sizeof(monitor)};
    GetMonitorInfoW(MonitorFromWindow(FindWindowW(L"Shell_TrayWnd", nullptr), MONITOR_DEFAULTTOPRIMARY),
        &monitor);
    return monitor;
}

// Windows' accessibility setting for how long notifications stay, in milliseconds.
UINT Duration()
{
    ULONG seconds = 0;
    if (!SystemParametersInfoW(SPI_GETMESSAGEDURATION, 0, &seconds, 0) || seconds == 0)
    {
        seconds = defaultDuration;
    }
    return seconds * 1000;
}

// The window ships the Lucide font beside the engine; sharing its copy keeps
// the engine small.
std::filesystem::path IconFont()
{
    return std::filesystem::path(Executable()).parent_path() / L"Lucide" / L"Fonts" / L"lucide.ttf";
}
}

// MSAA's simple children expose a popup window's painted parts without a
// second control tree; Windows' MSAA-to-UIA proxy makes them available to UI
// Automation clients, and IAccessibleEx adds the AutomationId that MSAA lacks.
// Popup decides every part, its state and what it does; this class only
// answers in those terms.
class Popup::Accessibility final : public Microsoft::WRL::RuntimeClass<
    Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
    Microsoft::WRL::ChainInterfaces<IAccessible, IDispatch>, IServiceProvider, IAccessibleEx,
    IRawElementProviderSimple>
{
public:
    Accessibility(Popup& popup, HWND window) : popup_(&popup), window_(window)
    {
        CreateStdAccessibleObject(window, OBJID_CLIENT, IID_PPV_ARGS(&standard_));
    }

    // UI Automation needs a separate object for each child of `parent`.
    Accessibility(Microsoft::WRL::ComPtr<Accessibility> parent, long child)
        : window_(parent->window_), standard_(parent->standard_), parent_(std::move(parent)), child_(child)
    {
    }

    void Disconnect() { popup_ = nullptr; }

    HRESULT STDMETHODCALLTYPE GetTypeInfoCount(UINT* count) override
    {
        return standard_ ? standard_->GetTypeInfoCount(count) : E_NOTIMPL;
    }
    HRESULT STDMETHODCALLTYPE GetTypeInfo(UINT index, LCID locale, ITypeInfo** info) override
    {
        return standard_ ? standard_->GetTypeInfo(index, locale, info) : E_NOTIMPL;
    }
    HRESULT STDMETHODCALLTYPE GetIDsOfNames(REFIID iid, LPOLESTR* names, UINT count, LCID locale,
        DISPID* ids) override
    {
        return standard_ ? standard_->GetIDsOfNames(iid, names, count, locale, ids) : E_NOTIMPL;
    }
    HRESULT STDMETHODCALLTYPE Invoke(DISPID id, REFIID iid, LCID locale, WORD flags,
        DISPPARAMS* parameters, VARIANT* result, EXCEPINFO* exception, UINT* error) override
    {
        Microsoft::WRL::ComPtr<ITypeInfo> info;
        auto status = GetTypeInfo(0, locale, &info);
        return SUCCEEDED(status) ? DispInvoke(static_cast<IAccessible*>(this), info.Get(), id, flags,
            parameters, result, exception, error) : status;
    }
    HRESULT STDMETHODCALLTYPE get_accParent(IDispatch** parent) override
    {
        if (!parent)
        {
            return E_POINTER;
        }
        *parent = nullptr;
        if (!Owner())
        {
            return CO_E_OBJNOTCONNECTED;
        }
        return standard_ ? standard_->get_accParent(parent) : S_FALSE;
    }
    HRESULT STDMETHODCALLTYPE get_accChildCount(long* count) override
    {
        if (!count)
        {
            return E_POINTER;
        }
        auto owner = Owner();
        *count = owner ? static_cast<long>(owner->Elements(window_).size()) - 1 : 0;
        return owner ? S_OK : CO_E_OBJNOTCONNECTED;
    }
    HRESULT STDMETHODCALLTYPE get_accChild(VARIANT child, IDispatch** object) override
    {
        if (!object)
        {
            return E_POINTER;
        }
        *object = nullptr;
        if (!Find(child))
        {
            return E_INVALIDARG;
        }
        return child.lVal == CHILDID_SELF ? QueryInterface(IID_PPV_ARGS(object)) : S_FALSE;
    }
    HRESULT STDMETHODCALLTYPE get_accName(VARIANT child, BSTR* name) override
    {
#ifdef _DEBUG
        // Shows which thread and apartment serve accessibility, which must be
        // the window's thread in its single-threaded apartment.
        APTTYPE apartment{};
        APTTYPEQUALIFIER qualifier{};
        std::wstring kind = L"none";
        if (SUCCEEDED(CoGetApartmentType(&apartment, &qualifier)))
        {
            kind = std::to_wstring(static_cast<int>(apartment)) + L"." + std::to_wstring(static_cast<int>(qualifier));
        }
        OutputDebugStringW((L"TinyTorrent accessibility: thread " + std::to_wstring(GetCurrentThreadId()) +
            L", window thread " + std::to_wstring(GetWindowThreadProcessId(window_, nullptr)) + L", apartment " +
            kind + L"\n").c_str());
#endif
        auto element = Find(child);
        return element ? Text(element->name, name) : Missing(name);
    }
    HRESULT STDMETHODCALLTYPE get_accValue(VARIANT, BSTR* value) override { return Empty(value); }
    HRESULT STDMETHODCALLTYPE get_accDescription(VARIANT child, BSTR* text) override
    {
        auto element = Find(child);
        return element ? Text(element->description, text) : Missing(text);
    }
    HRESULT STDMETHODCALLTYPE get_accHelp(VARIANT, BSTR* text) override { return Empty(text); }
    HRESULT STDMETHODCALLTYPE get_accKeyboardShortcut(VARIANT, BSTR* text) override { return Empty(text); }
    HRESULT STDMETHODCALLTYPE get_accHelpTopic(BSTR* file, VARIANT, long* topic) override
    {
        if (!topic)
        {
            return E_POINTER;
        }
        *topic = -1;
        return Empty(file);
    }
    HRESULT STDMETHODCALLTYPE get_accRole(VARIANT child, VARIANT* role) override
    {
        return Number(child, role, &Element::role);
    }
    HRESULT STDMETHODCALLTYPE get_accState(VARIANT child, VARIANT* state) override
    {
        return Number(child, state, &Element::state);
    }
    HRESULT STDMETHODCALLTYPE get_accFocus(VARIANT* focus) override
    {
        if (!focus)
        {
            return E_POINTER;
        }
        VariantInit(focus);
        auto owner = Owner();
        if (!owner)
        {
            return CO_E_OBJNOTCONNECTED;
        }
        auto focused = owner->FocusedChild(window_);
        if (!focused)
        {
            return S_FALSE;
        }
        focus->vt = VT_I4;
        focus->lVal = *focused;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE get_accSelection(VARIANT* selection) override
    {
        if (!selection)
        {
            return E_POINTER;
        }
        VariantInit(selection);
        auto owner = Owner();
        if (!owner)
        {
            return CO_E_OBJNOTCONNECTED;
        }
        auto elements = owner->Elements(window_);
        for (std::size_t child = 1; child < elements.size(); ++child)
        {
            if (elements[child].state & STATE_SYSTEM_SELECTED)
            {
                selection->vt = VT_I4;
                selection->lVal = static_cast<long>(child);
                return S_OK;
            }
        }
        return S_FALSE;
    }
    HRESULT STDMETHODCALLTYPE get_accDefaultAction(VARIANT child, BSTR* action) override
    {
        auto element = Find(child);
        return element ? Text(element->defaultAction, action) : Missing(action);
    }
    HRESULT STDMETHODCALLTYPE accSelect(long flags, VARIANT child) override
    {
        if (!Find(child) || !flags || (flags & ~(SELFLAG_TAKEFOCUS | SELFLAG_TAKESELECTION)))
        {
            return E_INVALIDARG;
        }
        return Owner()->Select(window_, child.lVal, flags) ? S_OK : DISP_E_MEMBERNOTFOUND;
    }
    HRESULT STDMETHODCALLTYPE accLocation(long* left, long* top, long* width, long* height,
        VARIANT child) override
    {
        if (!left || !top || !width || !height)
        {
            return E_POINTER;
        }
        *left = *top = *width = *height = 0;
        auto element = Find(child);
        if (!element)
        {
            return E_INVALIDARG;
        }
        auto bounds = element->bounds;
        MapWindowPoints(window_, nullptr, reinterpret_cast<POINT*>(&bounds), 2);
        *left = bounds.left;
        *top = bounds.top;
        *width = bounds.right - bounds.left;
        *height = bounds.bottom - bounds.top;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE accNavigate(long direction, VARIANT start, VARIANT* destination) override
    {
        if (!destination)
        {
            return E_POINTER;
        }
        VariantInit(destination);
        if (!Find(start))
        {
            return E_INVALIDARG;
        }
        long child = 0;
        auto count = static_cast<long>(Owner()->Elements(window_).size()) - 1;
        if (start.lVal == CHILDID_SELF && direction == NAVDIR_FIRSTCHILD)
        {
            child = 1;
        }
        else if (start.lVal == CHILDID_SELF && direction == NAVDIR_LASTCHILD)
        {
            child = count;
        }
        else if (start.lVal && (direction == NAVDIR_NEXT || direction == NAVDIR_DOWN))
        {
            child = start.lVal + 1;
        }
        else if (start.lVal && (direction == NAVDIR_PREVIOUS || direction == NAVDIR_UP))
        {
            child = start.lVal - 1;
        }
        if (child < 1 || child > count)
        {
            return S_FALSE;
        }
        destination->vt = VT_I4;
        destination->lVal = child;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE accHitTest(long x, long y, VARIANT* child) override
    {
        if (!child)
        {
            return E_POINTER;
        }
        VariantInit(child);
        auto owner = Owner();
        if (!owner)
        {
            return CO_E_OBJNOTCONNECTED;
        }
        POINT point{x, y};
        ScreenToClient(window_, &point);
        auto elements = owner->Elements(window_);
        if (!PtInRect(&elements.front().bounds, point))
        {
            return S_FALSE;
        }
        child->vt = VT_I4;
        child->lVal = CHILDID_SELF;
        for (std::size_t index = 1; index < elements.size(); ++index)
        {
            if (PtInRect(&elements[index].bounds, point))
            {
                child->lVal = static_cast<long>(index);
                break;
            }
        }
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE accDoDefaultAction(VARIANT child) override
    {
        auto element = Find(child);
        if (!element)
        {
            return E_INVALIDARG;
        }
        if (element->defaultAction.empty())
        {
            return DISP_E_MEMBERNOTFOUND;
        }
        // Let the accessibility call return before the part's command can
        // destroy its window.
        PostMessageW(window_, WM_COMMAND, static_cast<WPARAM>(child.lVal), 0);
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE put_accName(VARIANT, BSTR) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE put_accValue(VARIANT, BSTR) override { return E_NOTIMPL; }

    HRESULT STDMETHODCALLTYPE QueryService(REFGUID service, REFIID iid, void** object) override
    {
        if (!object)
        {
            return E_POINTER;
        }
        *object = nullptr;
        return service == __uuidof(IAccessibleEx) ? QueryInterface(iid, object) : E_NOINTERFACE;
    }

    HRESULT STDMETHODCALLTYPE GetObjectForChild(long child, IAccessibleEx** object) override
    {
        if (!object)
        {
            return E_POINTER;
        }
        *object = nullptr;
        if (parent_ || child == CHILDID_SELF || !Find(child))
        {
            return E_INVALIDARG;
        }
        auto element = Microsoft::WRL::Make<Accessibility>(Microsoft::WRL::ComPtr<Accessibility>(this), child);
        if (!element)
        {
            return E_OUTOFMEMORY;
        }
        *object = element.Detach();
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetIAccessiblePair(IAccessible** accessible, long* child) override
    {
        if (!accessible || !child)
        {
            return E_POINTER;
        }
        *accessible = parent_ ? static_cast<IAccessible*>(parent_.Get()) : static_cast<IAccessible*>(this);
        (*accessible)->AddRef();
        *child = child_;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetRuntimeId(SAFEARRAY** id) override
    {
        if (!id)
        {
            return E_POINTER;
        }
        // Unique within the window, as UI Automation requires.
        *id = SafeArrayCreateVector(VT_I4, 0, 2);
        if (!*id)
        {
            return E_OUTOFMEMORY;
        }
        LONG index = 0;
        int part = UiaAppendRuntimeId;
        SafeArrayPutElement(*id, &index, &part);
        index = 1;
        part = child_;
        SafeArrayPutElement(*id, &index, &part);
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE ConvertReturnedElement(IRawElementProviderSimple*, IAccessibleEx** object) override
    {
        if (object)
        {
            *object = nullptr;
        }
        return E_NOTIMPL;
    }

    HRESULT STDMETHODCALLTYPE get_ProviderOptions(ProviderOptions* options) override
    {
        if (!options)
        {
            return E_POINTER;
        }
        *options = ProviderOptions_ServerSideProvider;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetPatternProvider(PATTERNID, IUnknown** pattern) override
    {
        if (!pattern)
        {
            return E_POINTER;
        }
        *pattern = nullptr;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetPropertyValue(PROPERTYID property, VARIANT* value) override
    {
        if (!value)
        {
            return E_POINTER;
        }
        VariantInit(value);
        auto element = Find(child_);
        if (!element)
        {
            return UIA_E_ELEMENTNOTAVAILABLE;
        }
        // Every other property comes from IAccessible.
        if (property == UIA_AutomationIdPropertyId && !element->automationId.empty())
        {
            value->bstrVal = SysAllocString(element->automationId.c_str());
            if (!value->bstrVal)
            {
                return E_OUTOFMEMORY;
            }
            value->vt = VT_BSTR;
        }
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE get_HostRawElementProvider(IRawElementProviderSimple** host) override
    {
        if (!host)
        {
            return E_POINTER;
        }
        *host = nullptr;
        return S_OK;
    }

private:
    Popup* Owner() const { return parent_ ? parent_->popup_ : popup_; }
    std::optional<Element> Find(long child) const
    {
        auto owner = Owner();
        if (!owner || child < CHILDID_SELF)
        {
            return std::nullopt;
        }
        auto elements = owner->Elements(window_);
        if (static_cast<std::size_t>(child) >= elements.size())
        {
            return std::nullopt;
        }
        return std::move(elements[child]);
    }
    std::optional<Element> Find(VARIANT child) const
    {
        return child.vt == VT_I4 ? Find(child.lVal) : std::nullopt;
    }
    HRESULT Number(VARIANT child, VARIANT* number, long Element::* field) const
    {
        if (!number)
        {
            return E_POINTER;
        }
        VariantInit(number);
        auto element = Find(child);
        if (!element)
        {
            return E_INVALIDARG;
        }
        number->vt = VT_I4;
        number->lVal = (*element).*field;
        return S_OK;
    }
    static HRESULT Text(std::wstring const& text, BSTR* result)
    {
        if (text.empty())
        {
            return Empty(result);
        }
        if (!result)
        {
            return E_POINTER;
        }
        *result = SysAllocString(text.c_str());
        return *result ? S_OK : E_OUTOFMEMORY;
    }
    static HRESULT Empty(BSTR* text)
    {
        if (!text)
        {
            return E_POINTER;
        }
        *text = nullptr;
        return S_FALSE;
    }
    static HRESULT Missing(BSTR* text)
    {
        if (text)
        {
            *text = nullptr;
        }
        return text ? E_INVALIDARG : E_POINTER;
    }
    // Null once the window is destroyed; a child's comes from its parent.
    Popup* popup_ = nullptr;
    HWND window_;
    Microsoft::WRL::ComPtr<IAccessible> standard_;
    Microsoft::WRL::ComPtr<Accessibility> parent_;
    long child_ = CHILDID_SELF;
};

Popup::Popup(Strings const& strings) : strings_(strings)
{
    WNDCLASSW type{};
    type.lpfnWndProc = Procedure;
    type.hInstance = GetModuleHandleW(nullptr);
    type.hIcon = LoadIconW(type.hInstance, MAKEINTRESOURCEW(IDI_TINYTORRENT));
    type.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    type.lpszClassName = windowClass;
    RegisterClassW(&type);
    type.lpfnWndProc = MenuProcedure;
    type.lpszClassName = menuClass;
    RegisterClassW(&type);
    hasIcons_ = AddFontResourceExW(IconFont().c_str(), FR_PRIVATE, nullptr) > 0;
}

Popup::~Popup()
{
    Close();
    if (hasIcons_)
    {
        RemoveFontResourceExW(IconFont().c_str(), FR_PRIVATE, nullptr);
    }
}

void Popup::Show(Content notice)
{
    // A prompt's or an interactive notice's window has other styles. Closing
    // it before the new content is stored keeps its teardown on its own
    // content.
    if (asking_ || Interactive() != (notice.stay == NoticeStay::Interactive))
    {
        Close();
    }
    // The menu belongs to the content it was opened for.
    if (menu_)
    {
        DestroyWindow(menu_);
    }
    // Dismiss only closes the notice, which Invoke does before running it.
    notice.actions.push_back({.glyph = dismissGlyph, .label = strings_.Text("notification", "dismiss"),
        .run = [] {}, .automationId = L"Dismiss"});
    content_ = std::move(notice);
    Present();
    if (!window_)
    {
        return;
    }
    // Screen readers announce the alert also when the notice takes no focus.
    NotifyWinEvent(EVENT_SYSTEM_ALERT, window_, OBJID_CLIENT, CHILDID_SELF);
    if (Interactive())
    {
        Focus(Answer());
        SetForegroundWindow(window_);
        SetFocus(window_);
    }
    ScheduleClose();
}

void Popup::Ask(Prompt prompt)
{
    // A notice's window has other styles, so it closes before the prompt
    // replaces the content. Its More menu closes with it and reads the content
    // it was opened for.
    auto asking = asking_;
    if (!asking)
    {
        Close();
    }
    Content content{.title = std::move(prompt.title), .body = std::move(prompt.body),
        .location = std::move(prompt.location), .stay = NoticeStay::Interactive};
    content.actions.push_back(std::move(prompt.answer));
    for (auto& action : prompt.others)
    {
        content.actions.push_back(std::move(action));
    }
    if (!asking)
    {
        content_ = std::move(content);
        asking_ = true;
        Present();
        if (!window_)
        {
            asking_ = false;
            return;
        }
        Focus(Answer());
        SetForegroundWindow(window_);
        SetFocus(window_);
        return;
    }
    auto previous = std::exchange(content_, std::move(content));
    Layout();
    SetWindowTextW(window_, content_.title.c_str());
    auto usable = [this](std::size_t action)
    {
        return action < content_.actions.size() && content_.actions[action].enabled;
    };
    // A button that becomes disabled loses its press and the focus, so it
    // never shows pressed or runs when it is enabled again.
    if (pressed_ && pressed_->part == PopupPart::Action && !usable(pressed_->index))
    {
        pressed_.reset();
    }
    if (focused_ && !usable(*focused_))
    {
        Focus(std::nullopt);
    }
    if (!focused_)
    {
        Focus(Answer());
    }
    for (std::size_t index = 0; index < content_.actions.size(); ++index)
    {
        if (index >= previous.actions.size() || previous.actions[index].enabled != content_.actions[index].enabled)
        {
            NotifyWinEvent(EVENT_OBJECT_STATECHANGE, window_, OBJID_CLIENT, static_cast<LONG>(index + 1));
        }
    }
    if (previous.title != content_.title || previous.body != content_.body)
    {
        NotifyWinEvent(EVENT_OBJECT_NAMECHANGE, window_, OBJID_CLIENT, CHILDID_SELF);
        NotifyWinEvent(EVENT_SYSTEM_ALERT, window_, OBJID_CLIENT, CHILDID_SELF);
    }
}

void Popup::Create()
{
    // Created hidden on the taskbar's monitor, so it has that monitor's DPI
    // before Layout sizes it. The frame styles keep DWM's rounded corners and
    // shadow; WM_NCCALCSIZE removes their chrome. A notice stays above other
    // windows without a taskbar button, and only an interactive one takes the
    // focus. A prompt has a taskbar button and an Alt+Tab entry, so the person
    // can return to it, and is not topmost, so it never covers a window the
    // person turns to, such as the other TinyTorrent's own confirmation.
    auto work = Taskbar().rcWork;
    DWORD style = WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
    if (asking_)
    {
        style = 0;
    }
    else if (content_.stay != NoticeStay::Interactive)
    {
        style |= WS_EX_NOACTIVATE;
    }
    CreateWindowExW(style, windowClass, productName, WS_POPUP | WS_CAPTION | WS_THICKFRAME,
        (work.left + work.right) / 2, (work.top + work.bottom) / 2, 1, 1, nullptr, nullptr,
        GetModuleHandleW(nullptr), this);
    if (!window_)
    {
        return;
    }
    // A notice is mostly inactive, so the tooltip shows without activation.
    tooltip_ = CreateWindowExW(WS_EX_TOPMOST, TOOLTIPS_CLASSW, nullptr, WS_POPUP | TTS_ALWAYSTIP | TTS_NOPREFIX,
        CW_USEDEFAULT, CW_USEDEFAULT, CW_USEDEFAULT, CW_USEDEFAULT, window_, nullptr, GetModuleHandleW(nullptr),
        nullptr);
}

bool Popup::Interactive() const
{
    return window_ && content_.stay == NoticeStay::Interactive;
}

std::size_t Popup::Answer() const
{
    // A prompt's answer comes first, and Dismiss last.
    return asking_ ? 0 : content_.actions.size() - 1;
}

void Popup::Present()
{
    if (!window_)
    {
        Create();
    }
    if (!window_)
    {
        return;
    }
    auto shown = IsWindowVisible(window_) != FALSE;
    // New content stops a slide out, so the popup stays for its full time.
    KillTimer(window_, closeTimer);
    StopSlide();
    Layout();
    acrylic_.Apply(window_, theme_);
    // The taskbar and Alt+Tab name a prompt by its window text; accessibility
    // reads the whole content from the popup's own object.
    SetWindowTextW(window_, content_.title.c_str());
    if (!shown && Slides())
    {
        StartSlide(false);
    }
    ShowWindow(window_, Interactive() ? SW_SHOW : SW_SHOWNOACTIVATE);
}

void Popup::Close()
{
    if (window_)
    {
        DestroyWindow(window_);
    }
    DeleteFonts();
}

void Popup::ScheduleClose()
{
    if (content_.stay == NoticeStay::Timed && !tracking_ && !menu_)
    {
        SetTimer(window_, closeTimer, Duration(), nullptr);
    }
}

void Popup::SetTheme(Theme theme)
{
    if (theme == theme_)
    {
        return;
    }
    theme_ = theme;
    if (window_)
    {
        acrylic_.Apply(window_, theme_);
    }
    if (menu_)
    {
        acrylic_.Apply(menu_, theme_);
    }
}

void Popup::DeleteFonts()
{
    for (auto font : {&font_, &caption_, &headline_, &glyphs_, &icons_})
    {
        if (*font)
        {
            DeleteObject(*font);
            *font = nullptr;
        }
    }
}

// Sizes the popup to its text and places it in the corner of the work area
// beside the notification area.
void Popup::Layout()
{
    auto dpi = GetDpiForWindow(window_);
    DeleteFonts();
    NONCLIENTMETRICSW metrics{sizeof(metrics)};
    SystemParametersInfoForDpi(SPI_GETNONCLIENTMETRICS, sizeof(metrics), &metrics, 0, dpi);
    metrics.lfMessageFont.lfHeight = -Scale(captionSize, dpi);
    caption_ = CreateFontIndirectW(&metrics.lfMessageFont);
    metrics.lfMessageFont.lfHeight = -Scale(fontSize, dpi);
    font_ = CreateFontIndirectW(&metrics.lfMessageFont);
    auto headline = metrics.lfMessageFont;
    headline.lfWeight = FW_SEMIBOLD;
    headline_ = CreateFontIndirectW(&headline);
    LOGFONTW glyph{};
    glyph.lfHeight = -Scale(glyphSize, dpi);
    glyph.lfCharSet = DEFAULT_CHARSET;
    wcscpy_s(glyph.lfFaceName, L"Segoe MDL2 Assets");
    glyphs_ = CreateFontIndirectW(&glyph);
    glyph.lfHeight = -Scale(menuIconSize, dpi);
    wcscpy_s(glyph.lfFaceName, L"Lucide");
    icons_ = CreateFontIndirectW(&glyph);

    auto width = Scale(popupWidth, dpi);
    auto inset = Scale(padding, dpi);
    auto device = GetDC(window_);
    // The popup widens until every button holds its whole label, because
    // the buttons share equal widths and a cut action label hides the action.
    if (!content_.actions.empty())
    {
        auto previous = SelectObject(device, font_);
        LONG widest = 0;
        for (auto const& action : content_.actions)
        {
            SIZE label{};
            GetTextExtentPoint32W(device, action.label.c_str(), static_cast<int>(action.label.size()), &label);
            widest = std::max(widest, label.cx);
        }
        SelectObject(device, previous);
        auto icon = hasIcons_ ? Scale(actionIconSize, dpi) + Scale(actionIconGap, dpi) : 0;
        auto count = static_cast<LONG>(content_.actions.size());
        auto button = widest + icon + 2 * Scale(actionInset, dpi);
        width = std::max<LONG>(width, 2 * inset + count * button + (count - 1) * Scale(actionSpacing, dpi));
    }
    close_ = {width - Scale(closeInset + closeSize, dpi), Scale(closeInset, dpi), width - Scale(closeInset, dpi),
        Scale(closeInset + closeSize, dpi)};
    more_ = {close_.left - Scale(closeSize, dpi), close_.top, close_.left, close_.bottom};
    header_ = {inset, close_.top, close_.left, close_.bottom};
    auto measure = [&](std::wstring const& text, HFONT font, LONG top)
    {
        RECT bounds{inset, top, width - inset, top};
        if (text.empty())
        {
            return bounds;
        }
        auto previous = SelectObject(device, font);
        DrawTextW(device, text.c_str(), -1, &bounds, DT_CALCRECT | textFormat);
        SelectObject(device, previous);
        bounds.right = width - inset;
        bounds.bottom = std::min<LONG>(bounds.bottom, top + Scale(textLimit, dpi));
        return bounds;
    };
    title_ = measure(content_.title, headline_, header_.bottom + Scale(titleGap, dpi));
    body_ = measure(content_.body, font_, title_.bottom +
        (content_.body.empty() ? 0 : Scale(bodyGap, dpi)));
    auto bottom = body_.bottom;
    detail_ = {};
    auto lines = Lines();
    if (!lines.empty())
    {
        auto previous = SelectObject(device, caption_);
        TEXTMETRICW metrics{};
        GetTextMetricsW(device, &metrics);
        SelectObject(device, previous);
        auto height = std::min<LONG>(static_cast<LONG>(lines.size()) * (metrics.tmHeight + Scale(lineGap, dpi)) -
            Scale(lineGap, dpi), Scale(textLimit, dpi));
        detail_ = {inset, bottom + Scale(detailGap, dpi), width - inset,
            bottom + Scale(detailGap, dpi) + height};
        bottom = detail_.bottom;
    }
    actions_.clear();
    if (!content_.actions.empty())
    {
        auto count = static_cast<LONG>(content_.actions.size());
        auto spacing = Scale(actionSpacing, dpi);
        auto each = (width - 2 * inset - spacing * (count - 1)) / count;
        auto top = bottom + Scale(actionGap, dpi);
        for (LONG index = 0; index < count; ++index)
        {
            auto left = inset + index * (each + spacing);
            actions_.push_back({left, top, left + each, top + Scale(actionHeight, dpi)});
        }
        bottom = actions_.back().bottom;
    }
    SetTooltips();
    ReleaseDC(window_, device);
    auto height = bottom + inset;
    auto work = Taskbar().rcWork;
    place_ = {work.right - Scale(margin, dpi) - width, work.bottom - Scale(margin, dpi) - height};
    // A slide keeps moving the popup toward or away from its new place. A
    // prompt keeps its place in the z-order, so asking again never raises it
    // over a window the person turned to.
    auto move = sliding_ ? SWP_NOMOVE : 0;
    auto order = asking_ ? SWP_NOZORDER : 0;
    SetWindowPos(window_, HWND_TOPMOST, place_.x, place_.y, width, height, SWP_NOACTIVATE | move | order);
    InvalidateRect(window_, nullptr, FALSE);
}

// Windows' animation effects setting stills the popup, as it stills Windows'
// own notifications. A popup that would slide onto a monitor beyond the
// taskbar's right edge appears without the slide.
bool Popup::Slides() const
{
    BOOL animates = TRUE;
    SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION, 0, &animates, 0);
    if (!animates)
    {
        return false;
    }
    auto monitor = Taskbar();
    RECT bounds{};
    GetWindowRect(window_, &bounds);
    RECT path{monitor.rcMonitor.right, place_.y, monitor.rcWork.right + bounds.right - bounds.left,
        place_.y + bounds.bottom - bounds.top};
    return IsRectEmpty(&path) || !MonitorFromRect(&path, MONITOR_DEFAULTTONULL);
}

void Popup::StartSlide(bool leaving)
{
    sliding_ = Sliding{GetTickCount64(), leaving};
    Slide();
    SetTimer(window_, slideTimer, slideInterval, nullptr);
}

void Popup::StopSlide()
{
    KillTimer(window_, slideTimer);
    sliding_.reset();
}

// Moves the popup between its place and beyond the work area's edge, easing
// toward rest as Windows' own motion does. A popup that has left closes.
void Popup::Slide()
{
    auto elapsed = GetTickCount64() - sliding_->start;
    auto progress = std::min(1.0, static_cast<double>(elapsed) / slideDuration);
    auto away = sliding_->leaving ? progress * progress * progress : std::pow(1 - progress, 3);
    auto distance = Taskbar().rcWork.right - place_.x;
    SetWindowPos(window_, nullptr, place_.x + static_cast<int>(distance * away), place_.y, 0, 0,
        SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    if (progress < 1)
    {
        return;
    }
    if (sliding_->leaving)
    {
        Close();
        return;
    }
    StopSlide();
}

void Popup::Paint(HWND window)
{
    Acrylic::Canvas canvas(acrylic_, window);
    auto dpi = GetDpiForWindow(window);
    auto corner = Scale(radius, dpi);
    auto foreground = acrylic_.Foreground();
    auto size = Scale(iconSize, dpi);
    auto icon = content_.icon ? content_.icon : static_cast<HICON>(LoadImageW(GetModuleHandleW(nullptr),
        MAKEINTRESOURCEW(IDI_TINYTORRENT), IMAGE_ICON, size, size, LR_SHARED));
    DrawIconEx(canvas.Device(), header_.left, (header_.top + header_.bottom - size) / 2, icon, size, size, 0,
        nullptr, DI_NORMAL);
    // A mouse press shows while the pointer stays on its part, as on Windows'
    // buttons; a key press shows until its release.
    auto fill = [this](Target target, BYTE rest)
    {
        if (pressed_ == target && (GetCapture() != window_ || hover_ == target))
        {
            return pressedOpacity;
        }
        return hover_ == target ? hoverOpacity : rest;
    };
    // As in Windows' own notifications, More shows only while the pointer is
    // over the popup, so the message is all the person reads.
    auto showsMore = !content_.menu.empty() && (hover_ || menu_);
    RECT name{header_.left + size + Scale(iconGap, dpi), header_.top, showsMore ? more_.left : header_.right,
        header_.bottom};
    canvas.Text(productName, name, labelFormat, caption_, foreground);
    if (showsMore)
    {
        if (auto opacity = menu_ ? hoverOpacity : fill({PopupPart::More}, 0))
        {
            canvas.Fill(more_, foreground, opacity, corner);
        }
        canvas.Text(moreGlyph, more_, DT_SINGLELINE | DT_CENTER | DT_VCENTER, glyphs_, foreground);
    }
    if (auto opacity = fill({PopupPart::Close}, 0))
    {
        canvas.Fill(close_, foreground, opacity, corner);
    }
    canvas.Text(closeGlyph, close_, DT_SINGLELINE | DT_CENTER | DT_VCENTER, glyphs_, foreground);
    canvas.Text(content_.title, title_, textFormat, headline_, foreground);
    canvas.Text(content_.body, body_, textFormat, font_, foreground);
    auto previous = SelectObject(canvas.Device(), caption_);
    TEXTMETRICW metrics{};
    GetTextMetricsW(canvas.Device(), &metrics);
    SelectObject(canvas.Device(), previous);
    auto bounds = detail_;
    for (auto const& line : Lines())
    {
        if (bounds.top + metrics.tmHeight > detail_.bottom)
        {
            break;
        }
        bounds.bottom = bounds.top + metrics.tmHeight;
        auto ellipsis = line.find(L'\\') == std::wstring::npos ? DT_END_ELLIPSIS : DT_PATH_ELLIPSIS;
        canvas.Text(line, bounds, detailFormat | ellipsis, caption_, foreground);
        bounds.top = bounds.bottom + Scale(lineGap, dpi);
    }
    auto iconWidth = hasIcons_ ? Scale(actionIconSize, dpi) + Scale(actionIconGap, dpi) : 0;
    auto focused = GetFocus() == window_;
    previous = SelectObject(canvas.Device(), font_);
    for (std::size_t index = 0; index < actions_.size(); ++index)
    {
        auto const& action = content_.actions[index];
        auto button = actions_[index];
        auto opacity = action.enabled ? fill({PopupPart::Action, index}, buttonOpacity) : buttonOpacity;
        canvas.Fill(button, foreground, opacity, corner);
        if (auto edge = acrylic_.Edge())
        {
            canvas.Stroke(button, *edge, Scale(edgeWidth, dpi), corner);
        }
        // With no emphasised button, the ring is what shows where Enter goes.
        if (focused && focused_ == index)
        {
            auto ring = button;
            auto offset = Scale(focusWidth + focusGap, dpi);
            InflateRect(&ring, offset, offset);
            canvas.Stroke(ring, acrylic_.Focus(), Scale(focusWidth, dpi), corner + offset);
        }
        auto color = action.enabled ? foreground : acrylic_.Disabled();
        SIZE label{};
        GetTextExtentPoint32W(canvas.Device(), action.label.c_str(), static_cast<int>(action.label.size()), &label);
        // The icon and word are centred together, and a word too long for the
        // button ends in an ellipsis.
        auto inner = Scale(actionInset, dpi);
        auto left = std::max<LONG>(button.left + inner,
            (button.left + button.right - iconWidth - label.cx) / 2);
        if (hasIcons_)
        {
            RECT glyph{left, button.top, left + Scale(actionIconSize, dpi), button.bottom};
            canvas.Text(std::wstring(1, action.glyph), glyph, DT_SINGLELINE | DT_CENTER | DT_VCENTER, icons_, color);
        }
        RECT text{left + iconWidth, button.top, button.right - inner, button.bottom};
        canvas.Text(action.label, text, labelFormat, font_, color);
    }
    SelectObject(canvas.Device(), previous);
}

void Popup::Hover(std::optional<Target> target)
{
    if (target != hover_)
    {
        hover_ = target;
        InvalidateRect(window_, nullptr, FALSE);
    }
}

std::optional<Popup::Target> Popup::PartAt(POINT point) const
{
    RECT client{};
    GetClientRect(window_, &client);
    if (!PtInRect(&client, point))
    {
        return std::nullopt;
    }
    if (PtInRect(&close_, point))
    {
        return Target{PopupPart::Close};
    }
    if (!content_.menu.empty() && PtInRect(&more_, point))
    {
        return Target{PopupPart::More};
    }
    for (std::size_t index = 0; index < actions_.size(); ++index)
    {
        if (PtInRect(&actions_[index], point))
        {
            return Target{PopupPart::Action, index};
        }
    }
    return Target{PopupPart::Body};
}

void Popup::SetTooltips()
{
    if (!tooltip_)
    {
        return;
    }
    TOOLINFOW tool{sizeof(tool)};
    tool.hwnd = window_;
    // Each tool's id is its index, so the earlier ids are known.
    for (auto count = SendMessageW(tooltip_, TTM_GETTOOLCOUNT, 0, 0); count > 0; --count)
    {
        tool.uId = static_cast<UINT_PTR>(count - 1);
        SendMessageW(tooltip_, TTM_DELTOOLW, 0, reinterpret_cast<LPARAM>(&tool));
    }
    tool.uFlags = TTF_SUBCLASS;
    for (std::size_t index = 0; index < actions_.size(); ++index)
    {
        tool.uId = index;
        tool.rect = actions_[index];
        tool.lpszText = const_cast<LPWSTR>(content_.actions[index].tooltip.c_str());
        SendMessageW(tooltip_, TTM_ADDTOOLW, 0, reinterpret_cast<LPARAM>(&tool));
    }
    if (content_.location)
    {
        tool.uId = actions_.size();
        tool.rect = detail_;
        tool.lpszText = const_cast<LPWSTR>(content_.location->path.c_str());
        SendMessageW(tooltip_, TTM_ADDTOOLW, 0, reinterpret_cast<LPARAM>(&tool));
    }
}

std::vector<std::wstring> Popup::Lines() const
{
    std::vector<std::wstring> lines;
    if (content_.location)
    {
        lines.push_back(content_.location->caption);
        lines.push_back(content_.location->path.wstring());
    }
    std::wistringstream detail(content_.detail);
    for (std::wstring line; std::getline(detail, line);)
    {
        lines.push_back(std::move(line));
    }
    return lines;
}

// The window first, then the buttons, so a button's child ID is its index
// plus one, then Close and More.
std::vector<Popup::Target> Popup::Targets() const
{
    std::vector<Target> targets{{PopupPart::Body}};
    for (std::size_t index = 0; index < content_.actions.size(); ++index)
    {
        targets.push_back({PopupPart::Action, index});
    }
    targets.push_back({PopupPart::Close});
    // Listed whether or not the pointer shows it, so a person without a
    // pointer can reach the menu.
    if (!content_.menu.empty())
    {
        targets.push_back({PopupPart::More});
    }
    return targets;
}

std::vector<Popup::Element> Popup::Elements(HWND window) const
{
    RECT client{};
    GetClientRect(window, &client);
    long hidden = IsWindowVisible(window) ? 0 : STATE_SYSTEM_INVISIBLE;
    auto focused = GetFocus() == window;
    std::vector<Element> elements;
    if (window == menu_)
    {
        elements.push_back({.name = content_.menuTitle, .role = ROLE_SYSTEM_MENUPOPUP,
            .state = STATE_SYSTEM_FOCUSABLE | hidden | (focused && !menuHover_ ? STATE_SYSTEM_FOCUSED : 0),
            .bounds = client});
        for (std::size_t index = 0; index < content_.menu.size(); ++index)
        {
            auto const& item = content_.menu[index];
            long state = STATE_SYSTEM_FOCUSABLE | STATE_SYSTEM_SELECTABLE | hidden;
            if (menuHover_ == index)
            {
                state |= STATE_SYSTEM_SELECTED | (focused ? STATE_SYSTEM_FOCUSED : 0);
            }
            elements.push_back({.name = item.text, .automationId = item.automationId, .role = ROLE_SYSTEM_MENUITEM,
                .state = state, .defaultAction = item.text, .bounds = MenuBounds(index)});
        }
        return elements;
    }
    for (auto target : Targets())
    {
        switch (target.part)
        {
        case PopupPart::Body:
        {
            // The whole content, which an alert announces.
            auto text = content_.title + L"\n" + content_.body;
            for (auto const& line : Lines())
            {
                text += L"\n" + line;
            }
            long state = hidden;
            if (Interactive())
            {
                state |= STATE_SYSTEM_FOCUSABLE | (focused && !focused_ ? STATE_SYSTEM_FOCUSED : 0);
            }
            elements.push_back({.name = std::move(text), .role = asking_ ? ROLE_SYSTEM_DIALOG : ROLE_SYSTEM_ALERT,
                .state = state, .defaultAction = content_.select ? content_.title : std::wstring(),
                .bounds = client});
            break;
        }
        case PopupPart::Action:
        {
            auto const& action = content_.actions[target.index];
            long state = hidden;
            if (!action.enabled)
            {
                state |= STATE_SYSTEM_UNAVAILABLE;
            }
            else if (Interactive())
            {
                state |= STATE_SYSTEM_FOCUSABLE;
            }
            if (Interactive() && Answer() == target.index)
            {
                state |= STATE_SYSTEM_DEFAULT;
            }
            if (focused && focused_ == target.index)
            {
                state |= STATE_SYSTEM_FOCUSED;
            }
            if (pressed_ == target)
            {
                state |= STATE_SYSTEM_PRESSED;
            }
            auto bounds = target.index < actions_.size() ? actions_[target.index] : RECT{};
            elements.push_back({.name = action.label, .description = action.tooltip,
                .automationId = action.automationId, .role = ROLE_SYSTEM_PUSHBUTTON, .state = state,
                .defaultAction = action.label, .bounds = bounds});
            break;
        }
        case PopupPart::Close:
        {
            auto name = strings_.Text("notification", "close");
            elements.push_back({.name = name, .automationId = L"Close", .role = ROLE_SYSTEM_PUSHBUTTON,
                .state = hidden, .defaultAction = name, .bounds = close_});
            break;
        }
        case PopupPart::More:
        {
            auto name = strings_.Text("notification", "more");
            auto state = hidden | STATE_SYSTEM_HASPOPUP | (menu_ ? STATE_SYSTEM_EXPANDED : STATE_SYSTEM_COLLAPSED);
            elements.push_back({.name = name, .automationId = L"More", .role = ROLE_SYSTEM_BUTTONMENU,
                .state = state, .defaultAction = name, .bounds = more_});
            break;
        }
        }
    }
    return elements;
}

std::optional<long> Popup::FocusedChild(HWND window) const
{
    if (GetFocus() != window)
    {
        return std::nullopt;
    }
    auto item = window == menu_ ? menuHover_ : focused_;
    return item ? static_cast<long>(*item + 1) : CHILDID_SELF;
}

bool Popup::Select(HWND window, long child, long flags)
{
    if (window == menu_)
    {
        SetFocus(menu_);
        HoverMenu(child == CHILDID_SELF ? std::nullopt : std::optional<std::size_t>(child - 1));
        return true;
    }
    // Only a prompt or an interactive notice takes the focus, and then only
    // its window and its enabled buttons do.
    if (!Interactive() || !(flags & SELFLAG_TAKEFOCUS))
    {
        return false;
    }
    auto target = Targets()[child];
    if (target.part == PopupPart::Body)
    {
        Focus(std::nullopt);
    }
    else if (target.part != PopupPart::Action || !Focus(target.index))
    {
        return false;
    }
    SetFocus(window_);
    return true;
}

LRESULT Popup::Serve(Accessibility*& accessibility, HWND window, WPARAM first)
{
    if (!accessibility)
    {
        accessibility = Microsoft::WRL::Make<Accessibility>(*this, window).Detach();
    }
    return accessibility ? LresultFromObject(IID_IAccessible, first, static_cast<IAccessible*>(accessibility)) : 0;
}

bool Popup::Focus(std::optional<std::size_t> action)
{
    if (action && !content_.actions[*action].enabled)
    {
        return false;
    }
    focused_ = action;
    InvalidateRect(window_, nullptr, FALSE);
    if (GetFocus() == window_)
    {
        NotifyWinEvent(EVENT_OBJECT_FOCUS, window_, OBJID_CLIENT,
            action ? static_cast<LONG>(*action + 1) : CHILDID_SELF);
    }
    return true;
}

void Popup::Run(Target target)
{
    switch (target.part)
    {
    case PopupPart::More:
        // The More button toggles its menu, which a click on a notice does
        // not close, because a notice never takes the focus.
        if (menu_)
        {
            DestroyWindow(menu_);
        }
        else
        {
            OpenMenu();
        }
        return;
    case PopupPart::Close:
        Close();
        return;
    case PopupPart::Action:
        Invoke(target.index);
        return;
    case PopupPart::Body:
        // Only Close and Dismiss close a notice before its time, so selecting
        // its message does nothing unless the content acts on it, which a
        // prompt's never does.
        if (content_.select)
        {
            // A copy, because the code it runs may replace the content.
            auto select = content_.select;
            Close();
            select();
        }
        return;
    }
}

void Popup::Invoke(std::size_t index)
{
    if (!content_.actions[index].enabled)
    {
        return;
    }
    ReleaseCapture();
    // A copy, because the code it runs may replace the content.
    auto action = content_.actions[index];
    if (action.closes)
    {
        Close();
    }
    action.run();
}

void Popup::CopyPath()
{
    auto const& path = content_.location->path.native();
    auto bytes = (path.size() + 1) * sizeof(wchar_t);
    auto memory = GlobalAlloc(GMEM_MOVEABLE, bytes);
    if (!memory)
    {
        return;
    }
    auto text = GlobalLock(memory);
    if (!text)
    {
        GlobalFree(memory);
        return;
    }
    memcpy(text, path.c_str(), bytes);
    GlobalUnlock(memory);
    if (OpenClipboard(window_))
    {
        // The clipboard owns the memory once it accepts it.
        if (EmptyClipboard() && SetClipboardData(CF_UNICODETEXT, memory))
        {
            memory = nullptr;
        }
        CloseClipboard();
    }
    if (memory)
    {
        GlobalFree(memory);
    }
}

LRESULT CALLBACK Popup::Procedure(HWND window, UINT message, WPARAM first, LPARAM second)
{
    auto owner = reinterpret_cast<Popup*>(GetWindowLongPtrW(window, GWLP_USERDATA));
    if (message == WM_NCCREATE)
    {
        owner = static_cast<Popup*>(reinterpret_cast<CREATESTRUCTW*>(second)->lpCreateParams);
        owner->window_ = window;
        SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(owner));
    }
    return owner ? owner->Handle(window, message, first, second) :
        DefWindowProcW(window, message, first, second);
}

LRESULT Popup::Handle(HWND window, UINT message, WPARAM first, LPARAM second)
{
    switch (message)
    {
    case WM_GETOBJECT:
        if (static_cast<LONG>(second) == OBJID_CLIENT)
        {
            return Serve(accessibility_, window, first);
        }
        break;
    case WM_SETFOCUS:
        Focus(focused_);
        return 0;
    case WM_KILLFOCUS:
        pressed_.reset();
        InvalidateRect(window, nullptr, FALSE);
        return 0;
    case WM_KEYDOWN:
    case WM_KEYUP:
        // Only a prompt or an interactive notice has the focus.
        if (Interactive() && HandleKey(message, first, second))
        {
            return 0;
        }
        break;
    case WM_SYSKEYDOWN:
        // Alt+F4 held since before the popup had the focus would close it
        // unread.
        if (Interactive() && (second & keyHeld))
        {
            return 0;
        }
        break;
    case WM_CONTEXTMENU:
        if (content_.location)
        {
            POINT point{GET_X_LPARAM(second), GET_Y_LPARAM(second)};
            // The context menu key gives no point.
            if (point.x == -1 && point.y == -1)
            {
                point = {detail_.left, detail_.bottom};
                ClientToScreen(window, &point);
            }
            auto copy = strings_.Text("notification", "copy_path");
            auto menu = CreatePopupMenu();
            AppendMenuW(menu, MF_STRING, 1, copy.c_str());
            auto selected = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_NONOTIFY, point.x, point.y, window, nullptr);
            DestroyMenu(menu);
            if (selected == 1)
            {
                CopyPath();
            }
            return 0;
        }
        break;
    case WM_CLOSE:
        Close();
        return 0;
    case WM_COMMAND:
    {
        // An accessible default action, posted with the part's child ID.
        auto targets = Targets();
        if (first < targets.size())
        {
            Run(targets[first]);
        }
        return 0;
    }
    case WM_TIMER:
        if (first == closeTimer)
        {
            KillTimer(window, closeTimer);
            if (content_.stay != NoticeStay::Timed)
            {
                return 0;
            }
            if (Slides())
            {
                StartSlide(true);
            }
            else
            {
                Close();
            }
        }
        else if (first == slideTimer && sliding_)
        {
            Slide();
        }
        return 0;
    case WM_MOUSEACTIVATE:
        return Interactive() ? MA_ACTIVATE : MA_NOACTIVATE;
    case WM_MOUSEMOVE:
        // The popup stays while the pointer is over it, so it can be read.
        if (!tracking_)
        {
            TRACKMOUSEEVENT track{sizeof(track), TME_LEAVE, window, 0};
            tracking_ = TrackMouseEvent(&track) != FALSE;
            KillTimer(window, closeTimer);
        }
        if (sliding_ && sliding_->leaving)
        {
            StopSlide();
            SetWindowPos(window, nullptr, place_.x, place_.y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
        Hover(PartAt({GET_X_LPARAM(second), GET_Y_LPARAM(second)}));
        return 0;
    case WM_MOUSELEAVE:
        tracking_ = false;
        // A notice that takes no focus never has the foreground, so Windows
        // sends it no mouse input outside it, and the release of a press
        // dragged off it never arrives.
        if (!Interactive())
        {
            pressed_.reset();
        }
        Hover(std::nullopt);
        ScheduleClose();
        return 0;
    case WM_LBUTTONDOWN:
    {
        auto target = PartAt({GET_X_LPARAM(second), GET_Y_LPARAM(second)});
        // A disabled button takes no press.
        if (target && target->part == PopupPart::Action && !content_.actions[target->index].enabled)
        {
            target.reset();
        }
        // Captured first, because taking the capture from this window would
        // clear the new press.
        if (target)
        {
            SetCapture(window);
        }
        pressed_ = target;
        InvalidateRect(window, nullptr, FALSE);
        return 0;
    }
    case WM_LBUTTONUP:
    {
        // A copy, because releasing the capture clears the press.
        auto pressed = pressed_;
        pressed_.reset();
        ReleaseCapture();
        InvalidateRect(window, nullptr, FALSE);
        // Only a release on the pressed part acts, so a press dragged onto a
        // button, or off one, does nothing.
        if (pressed && pressed == PartAt({GET_X_LPARAM(second), GET_Y_LPARAM(second)}))
        {
            Run(*pressed);
        }
        return 0;
    }
    case WM_CAPTURECHANGED:
    {
        pressed_.reset();
        // Tracking starts again, so a pointer released outside the popup still
        // reports its leave, at once, and a notice schedules its close.
        TRACKMOUSEEVENT track{sizeof(track), TME_LEAVE, window, 0};
        tracking_ = TrackMouseEvent(&track) != FALSE;
        InvalidateRect(window, nullptr, FALSE);
        return 0;
    }
    case WM_CANCELMODE:
        pressed_.reset();
        break;
    case WM_NCCALCSIZE:
        return 0;
    case WM_NCHITTEST:
        return HTCLIENT;
    case WM_ERASEBKGND:
        return 0;
    case WM_PAINT:
        Paint(window);
        return 0;
    case WM_DPICHANGED:
    case WM_SETTINGCHANGE:
        Layout();
        acrylic_.Apply(window, theme_);
        return 0;
    case WM_SYSCOLORCHANGE:
    case WM_THEMECHANGED:
    case WM_DWMCOMPOSITIONCHANGED:
        acrylic_.Apply(window, theme_);
        return 0;
    case WM_NCDESTROY:
        if (accessibility_)
        {
            accessibility_->Disconnect();
            accessibility_->Release();
            accessibility_ = nullptr;
        }
        // The tooltip is owned by the popup, so Windows has destroyed it.
        window_ = nullptr;
        tooltip_ = nullptr;
        tracking_ = false;
        hover_.reset();
        pressed_.reset();
        focused_.reset();
        asking_ = false;
        sliding_.reset();
        break;
    }
    return DefWindowProcW(window, message, first, second);
}

bool Popup::HandleKey(UINT message, WPARAM key, LPARAM flags)
{
    if (message == WM_KEYUP)
    {
        // Space acts on its release, as on Windows' buttons.
        if (key != VK_SPACE || !focused_ || pressed_ != Target{PopupPart::Action, *focused_})
        {
            return false;
        }
        pressed_.reset();
        InvalidateRect(window_, nullptr, FALSE);
        Invoke(*focused_);
        return true;
    }
    // A repeat, or a key held since before the popup had the focus, could
    // answer it unread.
    if (flags & keyHeld)
    {
        return true;
    }
    switch (key)
    {
    case VK_ESCAPE:
        Close();
        return true;
    case VK_TAB:
    {
        pressed_.reset();
        auto count = content_.actions.size();
        auto back = GetKeyState(VK_SHIFT) < 0;
        // Without a focused button, Tab starts at the first and Shift+Tab at
        // the last.
        auto index = focused_.value_or(back ? 0 : count - 1);
        for (std::size_t step = 0; step < count; ++step)
        {
            index = (index + (back ? count - 1 : 1)) % count;
            if (Focus(index))
            {
                break;
            }
        }
        return true;
    }
    case VK_RETURN:
        Invoke(focused_.value_or(Answer()));
        return true;
    case VK_SPACE:
        if (focused_)
        {
            pressed_ = Target{PopupPart::Action, *focused_};
            InvalidateRect(window_, nullptr, FALSE);
        }
        return true;
    case 'C':
        if (GetKeyState(VK_CONTROL) < 0 && content_.location)
        {
            CopyPath();
            return true;
        }
        return false;
    }
    return false;
}

// The menu opens above the More button, aligned to its right edge, as
// Windows' own notification menu does, or below it when the work area leaves
// no room above. It takes the focus, so Escape and a click elsewhere close it.
void Popup::OpenMenu()
{
    auto dpi = GetDpiForWindow(window_);
    auto device = GetDC(window_);
    auto previous = SelectObject(device, font_);
    auto measure = [device](std::wstring const& text)
    {
        RECT bounds{};
        DrawTextW(device, text.c_str(), -1, &bounds, DT_CALCRECT | DT_SINGLELINE | DT_NOPREFIX);
        return bounds.right;
    };
    auto widest = measure(content_.menuTitle);
    for (auto const& item : content_.menu)
    {
        widest = std::max(widest, measure(item.text));
    }
    SelectObject(device, previous);
    ReleaseDC(window_, device);
    menuRow_ = Scale(menuRow, dpi);
    auto width = widest + Scale(2 * menuPadding + 2 * menuInset, dpi) + MenuIconsWidth(dpi);
    auto height = MenuItemsTop(dpi) + static_cast<int>(content_.menu.size()) * menuRow_ + Scale(menuPadding, dpi);
    auto anchor = more_;
    MapWindowPoints(window_, nullptr, reinterpret_cast<POINT*>(&anchor), 2);
    auto work = Taskbar().rcWork;
    auto x = std::max<LONG>(work.left, anchor.right - width);
    auto y = anchor.top - Scale(menuGap, dpi) - height;
    if (y < work.top)
    {
        y = anchor.bottom + Scale(menuGap, dpi);
    }
    // The frame styles keep DWM's rounded corners and shadow, as on the popup.
    CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_TOPMOST, menuClass, productName, WS_POPUP | WS_CAPTION | WS_THICKFRAME,
        x, y, width, height, window_, nullptr, GetModuleHandleW(nullptr), this);
    if (!menu_)
    {
        return;
    }
    KillTimer(window_, closeTimer);
    acrylic_.Apply(menu_, theme_);
    ShowWindow(menu_, SW_SHOW);
    // Without the focus, nothing would close the menu, and it would keep the
    // notice open. That happens when accessibility opens it while another
    // app is in front.
    if (!SetForegroundWindow(menu_))
    {
        DestroyWindow(menu_);
        return;
    }
    SetFocus(menu_);
    NotifyWinEvent(EVENT_SYSTEM_MENUPOPUPSTART, menu_, OBJID_CLIENT, CHILDID_SELF);
    NotifyWinEvent(EVENT_OBJECT_FOCUS, menu_, OBJID_CLIENT, CHILDID_SELF);
    InvalidateRect(window_, nullptr, FALSE);
}

void Popup::PaintMenu(HWND window)
{
    Acrylic::Canvas canvas(acrylic_, window);
    auto dpi = GetDpiForWindow(window);
    auto padding = Scale(menuPadding, dpi);
    auto inset = Scale(menuInset, dpi);
    auto width = canvas.Bounds().right;
    auto foreground = acrylic_.Foreground();
    // The title lines up with the items' text, as in Windows' menu.
    RECT title{padding + inset + MenuIconsWidth(dpi), padding, width - padding - inset, padding + menuRow_};
    canvas.Text(content_.menuTitle, title, labelFormat, font_, foreground);
    auto line = title.bottom + Scale(menuSeparator, dpi) / 2;
    canvas.Fill({0, line, width, line + Scale(1, dpi)}, foreground, dividerOpacity, 0);
    for (std::size_t index = 0; index < content_.menu.size(); ++index)
    {
        auto const& item = content_.menu[index];
        auto row = MenuBounds(index);
        if (menuHover_ == index)
        {
            canvas.Fill(row, foreground, hoverOpacity, Scale(radius, dpi));
        }
        auto left = row.left + inset;
        if (hasIcons_)
        {
            RECT icon{left, row.top, left + Scale(menuIconSize, dpi), row.bottom};
            canvas.Text(std::wstring(1, item.glyph), icon, DT_SINGLELINE | DT_CENTER | DT_VCENTER, icons_,
                foreground);
        }
        RECT text{left + MenuIconsWidth(dpi), row.top, row.right - inset, row.bottom};
        canvas.Text(item.text, text, labelFormat, font_, foreground);
    }
}

int Popup::MenuItemsTop(UINT dpi) const
{
    return Scale(menuPadding + menuSeparator, dpi) + menuRow_;
}

int Popup::MenuIconsWidth(UINT dpi) const
{
    return hasIcons_ ? Scale(menuIconSize + menuIconGap, dpi) : 0;
}

std::optional<std::size_t> Popup::MenuItemAt(POINT point) const
{
    RECT bounds{};
    GetClientRect(menu_, &bounds);
    auto dpi = GetDpiForWindow(menu_);
    auto padding = Scale(menuPadding, dpi);
    auto top = MenuItemsTop(dpi);
    if (point.x < padding || point.x >= bounds.right - padding || point.y < top)
    {
        return std::nullopt;
    }
    auto item = static_cast<std::size_t>((point.y - top) / menuRow_);
    if (item >= content_.menu.size())
    {
        return std::nullopt;
    }
    return item;
}

RECT Popup::MenuBounds(std::size_t item) const
{
    RECT bounds{};
    GetClientRect(menu_, &bounds);
    auto dpi = GetDpiForWindow(menu_);
    auto padding = Scale(menuPadding, dpi);
    auto top = MenuItemsTop(dpi) + static_cast<int>(item) * menuRow_;
    return {padding, top, bounds.right - padding, top + menuRow_};
}

void Popup::HoverMenu(std::optional<std::size_t> item)
{
    if (item != menuHover_)
    {
        menuHover_ = item;
        InvalidateRect(menu_, nullptr, FALSE);
        NotifyWinEvent(EVENT_OBJECT_FOCUS, menu_, OBJID_CLIENT,
            item ? static_cast<LONG>(*item + 1) : CHILDID_SELF);
    }
}

void Popup::Choose(std::size_t item)
{
    // A copy, because the code it runs may replace the content.
    auto run = content_.menu[item].run;
    Close();
    run();
}

LRESULT CALLBACK Popup::MenuProcedure(HWND window, UINT message, WPARAM first, LPARAM second)
{
    auto owner = reinterpret_cast<Popup*>(GetWindowLongPtrW(window, GWLP_USERDATA));
    if (message == WM_NCCREATE)
    {
        owner = static_cast<Popup*>(reinterpret_cast<CREATESTRUCTW*>(second)->lpCreateParams);
        owner->menu_ = window;
        SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(owner));
    }
    return owner ? owner->HandleMenu(window, message, first, second) :
        DefWindowProcW(window, message, first, second);
}

LRESULT Popup::HandleMenu(HWND window, UINT message, WPARAM first, LPARAM second)
{
    switch (message)
    {
    case WM_GETOBJECT:
        if (static_cast<LONG>(second) == OBJID_CLIENT)
        {
            return Serve(menuAccessibility_, window, first);
        }
        break;
    case WM_COMMAND:
        if (first > 0 && first <= content_.menu.size())
        {
            Choose(first - 1);
        }
        return 0;
    case WM_MOUSEMOVE:
    {
        TRACKMOUSEEVENT track{sizeof(track), TME_LEAVE, window, 0};
        TrackMouseEvent(&track);
        HoverMenu(MenuItemAt({GET_X_LPARAM(second), GET_Y_LPARAM(second)}));
        return 0;
    }
    case WM_MOUSELEAVE:
        HoverMenu(std::nullopt);
        return 0;
    case WM_LBUTTONUP:
        if (auto item = MenuItemAt({GET_X_LPARAM(second), GET_Y_LPARAM(second)}))
        {
            Choose(*item);
        }
        return 0;
    case WM_KEYDOWN:
    {
        auto count = content_.menu.size();
        if (first == VK_ESCAPE)
        {
            DestroyWindow(window);
        }
        else if (first == VK_DOWN)
        {
            HoverMenu(menuHover_ ? (*menuHover_ + 1) % count : 0);
        }
        else if (first == VK_UP)
        {
            HoverMenu(menuHover_ ? (*menuHover_ + count - 1) % count : count - 1);
        }
        else if ((first == VK_RETURN || first == VK_SPACE) && menuHover_)
        {
            Choose(*menuHover_);
        }
        return 0;
    }
    case WM_ACTIVATE:
        // A click elsewhere closes the menu, as it closes Windows' own.
        if (LOWORD(first) == WA_INACTIVE)
        {
            PostMessageW(window, WM_CLOSE, 0, 0);
        }
        break;
    case WM_NCCALCSIZE:
        return 0;
    case WM_NCHITTEST:
        return HTCLIENT;
    case WM_ERASEBKGND:
        return 0;
    case WM_PAINT:
        PaintMenu(window);
        return 0;
    case WM_SETTINGCHANGE:
    case WM_SYSCOLORCHANGE:
    case WM_THEMECHANGED:
    case WM_DWMCOMPOSITIONCHANGED:
        acrylic_.Apply(window, theme_);
        return 0;
    case WM_NCDESTROY:
        NotifyWinEvent(EVENT_SYSTEM_MENUPOPUPEND, window, OBJID_CLIENT, CHILDID_SELF);
        if (menuAccessibility_)
        {
            menuAccessibility_->Disconnect();
            menuAccessibility_->Release();
            menuAccessibility_ = nullptr;
        }
        menu_ = nullptr;
        menuHover_.reset();
        if (window_)
        {
            ScheduleClose();
            InvalidateRect(window_, nullptr, FALSE);
        }
        break;
    }
    return DefWindowProcW(window, message, first, second);
}
}
