#pragma once
#include "Engine.h"
#include <vector>

namespace tt
{
class Strings
{
public:
    explicit Strings(std::string language = DefaultLanguage());
    static std::string DefaultLanguage();
    // Whether `language` names a shipped catalogue exactly, as a saved
    // language setting must.
    static bool Supports(std::string_view language);
    std::string const& Language() const { return language_; }
    std::wstring Text(std::string const& group, std::string const& key) const;
    std::wstring Format(std::string const& group, std::string const& key,
        std::vector<std::wstring> const& values) const;
private:
    std::string language_;
    Json catalogue_;
};
}
