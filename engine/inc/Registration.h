#pragma once
#include "Engine.h"

namespace tt
{
// The word of each registration action in the protocol and on the command
// line.
inline constexpr std::pair<std::string_view, RegistrationAction> registrationActions[] = {
    {"observe", RegistrationAction::Observe},
    {"register_handlers", RegistrationAction::RegisterHandlers},
    {"unregister_handlers", RegistrationAction::UnregisterHandlers},
    {"enable_startup", RegistrationAction::EnableStartup},
    {"disable_startup", RegistrationAction::DisableStartup}};

class Registration
{
public:
    Outcome Execute(RegistrationAction action) const;
    // Who holds the handlers and startup entry, and the broken handlers, as
    // Settings shows them.
    Json Observe() const;
    void Repair() const;
    // The programs that torrent or magnet handlers start but that are
    // missing, whichever application registered them.
    std::vector<std::string> MissingPrograms() const;
    // Removes the all-users open command of each named class that starts a
    // missing program; needs an administrator.
    void RepairMachine(std::vector<std::wstring> const& classes) const;
private:
    void RegisterHandlers() const;
    void UnregisterHandlers() const;
};
}
