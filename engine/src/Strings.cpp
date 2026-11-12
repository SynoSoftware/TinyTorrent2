#include "Strings.h"
#include "Resources.h"
#include <windows.h>
#include <set>

namespace tt
{
namespace
{
// The shipped catalogues by language tag. English holds every message, and
// another catalogue replaces the messages it translates with the same
// placeholders, because English changes between translation refreshes.
constexpr std::pair<std::string_view, WORD> catalogues[] = {
    {"en", IDR_ENGLISH_TEXT},
    {"es", IDR_SPANISH_TEXT},
    {"nl", IDR_DUTCH_TEXT},
    {"zh", IDR_CHINESE_TEXT},
    {"pt", IDR_PORTUGUESE_TEXT},
    {"fr", IDR_FRENCH_TEXT}};

Json Catalogue(WORD resource)
{
    auto module = GetModuleHandleW(nullptr);
    auto found = FindResourceW(module, MAKEINTRESOURCEW(resource), RT_RCDATA);
    if (!found)
    {
        return {};
    }
    auto loaded = LoadResource(module, found);
    if (!loaded)
    {
        return {};
    }
    auto bytes = static_cast<char const*>(LockResource(loaded));
    return Json::parse(std::string_view(bytes, SizeofResource(module, found)), nullptr, false);
}

// The shipped catalogues of `tag` and of its parent languages, nearest first:
// pt-BR, then pt, when both ship.
std::vector<std::string_view> Shipped(std::string_view tag)
{
    std::vector<std::string_view> shipped;
    while (!tag.empty())
    {
        if (Parse(catalogues, tag))
        {
            shipped.push_back(tag);
        }
        auto separator = tag.rfind('-');
        tag = separator == std::string_view::npos ? std::string_view() : tag.substr(0, separator);
    }
    return shipped;
}

std::set<std::string> Placeholders(std::string const& text)
{
    std::set<std::string> found;
    for (auto open = text.find('{'); open != std::string::npos; open = text.find('{', open + 1))
    {
        auto close = text.find('}', open);
        if (close == std::string::npos)
        {
            break;
        }
        auto number = std::string_view(text).substr(open + 1, close - open - 1);
        if (!number.empty() && number.find_first_not_of("0123456789") == std::string_view::npos)
        {
            found.insert(std::string(number));
        }
    }
    return found;
}

// Replaces each message of `catalogue` that `translated` translates with the
// same placeholders.
void Translate(Json& catalogue, Json const& translated)
{
    if (!translated.is_object())
    {
        return;
    }
    for (auto const& [group, messages] : translated.items())
    {
        auto english = catalogue.find(group);
        if (english == catalogue.end() || !messages.is_object())
        {
            continue;
        }
        for (auto const& [key, message] : messages.items())
        {
            auto original = english->find(key);
            if (original != english->end() && original->is_string() && message.is_string() &&
                Placeholders(original->get<std::string>()) == Placeholders(message.get<std::string>()))
            {
                *original = message;
            }
        }
    }
}
}

// A message that the language lacks comes from its nearest shipped parent
// language, then English, so each shipped catalogue replaces English's
// messages in turn, from the most general language to the language itself.
Strings::Strings(std::string language)
    : language_(std::move(language)), catalogue_(Catalogue(IDR_ENGLISH_TEXT))
{
    auto shipped = Shipped(language_);
    for (auto tag = shipped.rbegin(); tag != shipped.rend(); ++tag)
    {
        if (auto resource = *Parse(catalogues, *tag); resource != IDR_ENGLISH_TEXT)
        {
            Translate(catalogue_, Catalogue(resource));
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
    auto shipped = Shipped(tag);
    return shipped.empty() ? "en" : std::string(shipped.front());
}

bool Strings::Supports(std::string_view language)
{
    return Parse(catalogues, language).has_value();
}

std::optional<std::wstring> Strings::Find(std::string const& group, std::string const& key) const
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
    return std::nullopt;
}

std::wstring Strings::Text(std::string const& group, std::string const& key) const
{
    return Find(group, key).value_or(Wide(group + "." + key));
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

std::wstring Strings::Number(double value, unsigned digits)
{
    wchar_t number[64];
    swprintf_s(number, L"%.*f", static_cast<int>(digits), value);
    wchar_t decimal[16] = L".";
    wchar_t thousand[16] = L",";
    wchar_t grouping[16] = L"3;0";
    LPCWSTR locale = LOCALE_NAME_USER_DEFAULT;
    GetLocaleInfoEx(locale, LOCALE_SDECIMAL, decimal, static_cast<int>(std::size(decimal)));
    GetLocaleInfoEx(locale, LOCALE_STHOUSAND, thousand, static_cast<int>(std::size(thousand)));
    GetLocaleInfoEx(locale, LOCALE_SGROUPING, grouping, static_cast<int>(std::size(grouping)));
    UINT groups = 0;
    for (auto character : std::wstring_view(grouping))
    {
        if (character == L'0') break;
        if (character != L';') groups = groups * 10 + character - L'0';
    }
    if (grouping[wcslen(grouping) - 1] != L'0') groups *= 10;
    NUMBERFMTW format{.NumDigits = digits, .LeadingZero = 1, .Grouping = groups,
        .lpDecimalSep = decimal, .lpThousandSep = thousand, .NegativeOrder = 1};
    wchar_t formatted[64];
    if (GetNumberFormatEx(locale, 0, number, &format, formatted, static_cast<int>(std::size(formatted))))
    {
        return formatted;
    }
    return number;
}
}
