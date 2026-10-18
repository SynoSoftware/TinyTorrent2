#pragma once
#include "Pipe.h"
#include "Strings.h"
#include <string>

namespace tt::desktop
{
// The name of the engine lock, the mutex that the running engine of the logon
// session `sid` holds.
std::wstring EngineLockName(std::wstring const& sid);

// Whether a product window of the logon session `sid` runs, including one
// that outlived its engine. Throws std::system_error when Windows cannot tell.
bool HasWindow(std::wstring const& sid);

// Asks the person whether to exit `other`, the running engine of another data
// folder, and returns true once that engine has ended and either its window
// has ended too or another engine holds the engine lock; false when the
// person cancels.
bool ExitOther(Strings const& strings, std::wstring const& sid, Forwarded const& other);
}
