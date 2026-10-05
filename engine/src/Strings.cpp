#include "Strings.h"
#include "Resources.h"
#include <windows.h>

namespace tiny
{
namespace
{
Json Catalogue(WORD resource)
{
    auto module = GetModuleHandleW(nullptr);
    auto found = FindResourceW(module, MAKEINTRESOURCEW(resource), RT_RCDATA);
    auto loaded = LoadResource(module, found);
    auto bytes = static_cast<char const*>(LockResource(loaded));
    return Json::parse(std::string_view(bytes, SizeofResource(module, found)), nullptr, false);
}
}

Strings::Strings(std::string language)
    : language_(std::move(language)), catalogue_(Catalogue(IDR_ENGLISH_TEXT))
{
    if (language_ == "es" || language_.starts_with("es-"))
    {
        auto translated = Catalogue(IDR_SPANISH_TEXT);
        if (translated.is_object())
        {
            catalogue_.merge_patch(translated);
        }
    }
}

// First use matches the Windows display language against the shipped
// catalogues and falls back to English.
std::string Strings::DefaultLanguage()
{
    return PRIMARYLANGID(GetUserDefaultUILanguage()) == LANG_SPANISH ? "es" : "en";
}

std::wstring Strings::Text(std::string const& group, std::string const& key) const
{
    auto messages = catalogue_.find(group);
    if (messages != catalogue_.end())
    {
        auto message = messages->find(key);
        if (message != messages->end() && message->is_string())
        {
            return Wide(message->get<std::string>());
        }
    }
    return Wide(group + "." + key);
}

std::wstring Strings::Format(std::string const& group, std::string const& key,
    std::vector<std::wstring> const& values) const
{
    // Markers are found only in the template, so a value that contains one,
    // such as a torrent name, stays as it is.
    auto text = Text(group, key);
    std::wstring result;
    size_t offset = 0;
    while (offset < text.size())
    {
        bool replaced = false;
        for (size_t index = 0; index < values.size() && !replaced; ++index)
        {
            auto marker = L"{" + std::to_wstring(index) + L"}";
            if (text.compare(offset, marker.size(), marker) == 0)
            {
                result += values[index];
                offset += marker.size();
                replaced = true;
            }
        }
        if (!replaced)
        {
            result += text[offset];
            ++offset;
        }
    }
    return result;
}
}
