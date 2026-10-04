#pragma once
#include "Engine.h"

namespace tiny
{
class Registration
{
public:
    Json Execute(std::string const& operation) const;
private:
    Json Observe() const;
    void Handlers(bool enabled) const;
    void Startup(bool enabled) const;
};
}
