#pragma once
#include "Engine.h"

namespace tt
{
class Registration
{
public:
    Json Execute(std::string const& name) const;
    void Repair() const;
    // The programs that torrent or magnet handlers start but that are
    // missing, whichever application registered them.
    std::vector<std::string> MissingPrograms() const;
    // Removes the all-users open command of each named class that starts a
    // missing program; needs an administrator.
    void RepairMachine(std::vector<std::wstring> const& classes) const;
private:
    Json Observe() const;
    void RegisterHandlers() const;
    void UnregisterHandlers() const;
};
}
