#include "Strings.h"
#include "Resources.h"
#include <windows.h>

namespace tt
{
namespace
{
// The shipped catalogues by language tag. English holds every message, and
// another catalogue replaces the messages it translates.
constexpr std::pair<std::string_view, WORD> catalogues[] = {
    {"en", IDR_ENGLISH_TEXT},
    {"es", IDR_SPANISH_TEXT}};

Json Catalogue(WORD resource)
{
    auto module = GetModuleHandleW(nullptr);
    auto found = FindResourceW(module, MAKEINTRESOURCEW(resource), RT_RCDATA);
    auto loaded = LoadResource(module, found);
    auto bytes = static_cast<char const*>(LockResource(loaded));
    return Json::parse(std::string_view(bytes, SizeofResource(module, found)), nullptr, false);
}

// A language tag such as `es-MX` asks for the catalogue that its primary
// subtag, `es`, names.
std::string_view Primary(std::string_view tag)
{
    return tag.substr(0, tag.find('-'));
}
}

Strings::Strings(std::string language)
    : language_(std::move(language)), catalogue_(Catalogue(IDR_ENGLISH_TEXT))
{
    auto resource = Parse(catalogues, Primary(language_));
    if (resource && *resource != IDR_ENGLISH_TEXT)
    {
        auto translated = Catalogue(*resource);
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
    wchar_t name[LOCALE_NAME_MAX_LENGTH] = {};
    LCIDToLocaleName(GetUserDefaultUILanguage(), name, LOCALE_NAME_MAX_LENGTH, LOCALE_ALLOW_NEUTRAL_NAMES);
    auto tag = Utf8(name);
    auto primary = std::string(Primary(tag));
    return Supports(primary) ? primary : "en";
}

bool Strings::Supports(std::string_view language)
{
    return Parse(catalogues, language).has_value();
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
