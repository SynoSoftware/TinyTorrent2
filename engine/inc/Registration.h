#pragma once
#include "Engine.h"

namespace tiny
{
class Registration
{
public:
    Json Execute(std::string const& name) const;
private:
    Json Observe() const;
    void RegisterHandlers() const;
    void UnregisterHandlers() const;
};
}
