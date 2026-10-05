#include "Desktop/PowerRequest.h"

namespace tiny::desktop
{
PowerRequest::PowerRequest(Strings const& strings) : strings_(strings) {}

PowerRequest::~PowerRequest()
{
    if (held_)
    {
        PowerClearRequest(handle_, PowerRequestSystemRequired);
    }
    if (handle_ != INVALID_HANDLE_VALUE)
    {
        CloseHandle(handle_);
    }
}

void PowerRequest::Update(Activity const& activity, bool exiting)
{
    SYSTEM_POWER_STATUS source{};
    bool transferring = activity.downloading || (activity.preventSleepSeeding && activity.seeding);
    Hold(!exiting && GetSystemPowerStatus(&source) && source.ACLineStatus == AC_LINE_ONLINE &&
        activity.preventSleep && transferring);
}

// Windows keeps the reason text it received when the request was created, so
// a new language needs a new request.
void PowerRequest::Translate()
{
    if (handle_ == INVALID_HANDLE_VALUE)
    {
        return;
    }
    bool held = held_;
    if (held_)
    {
        PowerClearRequest(handle_, PowerRequestSystemRequired);
    }
    CloseHandle(handle_);
    handle_ = INVALID_HANDLE_VALUE;
    held_ = false;
    Hold(held);
}

void PowerRequest::Hold(bool needed)
{
    if (needed == held_)
    {
        return;
    }
    if (!needed)
    {
        if (PowerClearRequest(handle_, PowerRequestSystemRequired))
        {
            held_ = false;
        }
        return;
    }
    if (handle_ == INVALID_HANDLE_VALUE)
    {
        auto reason = strings_.Text("power", "transfers");
        REASON_CONTEXT context{POWER_REQUEST_CONTEXT_VERSION, POWER_REQUEST_CONTEXT_SIMPLE_STRING};
        context.Reason.SimpleReasonString = reason.data();
        handle_ = PowerCreateRequest(&context);
    }
    if (handle_ != INVALID_HANDLE_VALUE && PowerSetRequest(handle_, PowerRequestSystemRequired))
    {
        held_ = true;
    }
}
}
