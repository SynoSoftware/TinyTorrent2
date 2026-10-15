#pragma once
#include <windows.h>
#include <memory>
#include <type_traits>

namespace tt
{
namespace release
{
struct Handle
{
    void operator()(HANDLE handle) const { CloseHandle(handle); }
};

struct Window
{
    void operator()(HWND window) const { DestroyWindow(window); }
};

struct Notification
{
    void operator()(HPOWERNOTIFY notification) const { UnregisterPowerSettingNotification(notification); }
};

struct Icon
{
    void operator()(HICON icon) const { DestroyIcon(icon); }
};
}

// Owners of Windows resources. A member releases its resource when its object
// is destroyed, so members are released in the reverse of their declaration
// order. Only handles that are null when invalid belong here: a file handle
// is owned after the check against INVALID_HANDLE_VALUE.
using OwnedHandle = std::unique_ptr<void, release::Handle>;
using OwnedWindow = std::unique_ptr<std::remove_pointer_t<HWND>, release::Window>;
using OwnedNotification = std::unique_ptr<void, release::Notification>;
using OwnedIcon = std::unique_ptr<std::remove_pointer_t<HICON>, release::Icon>;
}
