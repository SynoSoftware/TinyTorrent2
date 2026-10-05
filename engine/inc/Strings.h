#pragma once
#include "Engine.h"
#include <vector>

namespace tiny
{
class Strings
{
public:
    explicit Strings(std::string language = DefaultLanguage());
    static std::string DefaultLanguage();
    std::string const& Language() const { return language_; }
    std::wstring Text(std::string const& group, std::string const& key) const;
    std::wstring Format(std::string const& group, std::string const& key,
        std::vector<std::wstring> const& values) const;
private:
    std::string language_;
    Json catalogue_;
};
}
