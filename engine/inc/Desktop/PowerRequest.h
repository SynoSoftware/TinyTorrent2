#pragma once
#include "Strings.h"
#include <windows.h>

namespace tt::desktop
{
// Keeps Windows awake while torrents transfer on AC power, as the settings
// allow.
class PowerRequest
{
public:
    explicit PowerRequest(Strings const& strings);
    ~PowerRequest();
    PowerRequest(PowerRequest const&) = delete;
    PowerRequest& operator=(PowerRequest const&) = delete;
    void Update(Activity const& activity, bool exiting);
    void Translate();
private:
    void Hold(bool needed);
    Strings const& strings_;
    HANDLE handle_ = INVALID_HANDLE_VALUE;
    bool held_ = false;
};
}
