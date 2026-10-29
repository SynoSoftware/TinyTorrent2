#include "Tree.h"
#include "Torrent.h"
#include <Windows.h>
#include <algorithm>
#include <stdexcept>

namespace tt
{
namespace
{
// A query word is a run of letters and digits, as the app's FileName.Words
// splits text.
bool IsWordCharacter(wchar_t character)
{
    if (character < 0x80)
    {
        return (character >= L'a' && character <= L'z') || (character >= L'A' && character <= L'Z') ||
            (character >= L'0' && character <= L'9');
    }
    WORD type = 0;
    return GetStringTypeW(CT_CTYPE1, &character, 1, &type) && (type & (C1_ALPHA | C1_DIGIT));
}

std::wstring Normalized(std::wstring const& text, NORM_FORM form)
{
    if (text.empty())
    {
        return text;
    }
    auto length = static_cast<int>(text.size());
    // The first size is an estimate; a short buffer returns a new one.
    auto size = NormalizeString(form, text.c_str(), length, nullptr, 0);
    for (int attempt = 0; attempt < 4 && size > 0; ++attempt)
    {
        std::wstring result(static_cast<std::size_t>(size), L'\0');
        size = NormalizeString(form, text.c_str(), length, result.data(), size);
        if (size > 0)
        {
            result.resize(static_cast<std::size_t>(size));
            return result;
        }
        if (GetLastError() != ERROR_INSUFFICIENT_BUFFER)
        {
            break;
        }
        size = -size;
    }
    throw std::runtime_error("The search text could not be normalized.");
}

bool StartsWord(std::wstring_view text, std::wstring_view word)
{
    for (auto at = text.find(word); at != std::wstring_view::npos; at = text.find(word, at + 1))
    {
        if (at == 0 || !IsWordCharacter(text[at - 1]))
        {
            return true;
        }
    }
    return false;
}
}

Search::Search(std::string_view query)
{
    auto text = Normalize(query);
    for (std::size_t at = 0; at < text.size();)
    {
        if (!IsWordCharacter(text[at]))
        {
            ++at;
            continue;
        }
        auto end = at;
        while (end < text.size() && IsWordCharacter(text[end]))
        {
            ++end;
        }
        auto word = text.substr(at, end - at);
        if (std::find(words_.begin(), words_.end(), word) == words_.end())
        {
            words_.push_back(std::move(word));
        }
        at = end;
    }
    if (words_.size() > 64)
    {
        throw std::invalid_argument("A search has at most 64 words.");
    }
}

std::uint64_t Search::Found(std::wstring_view text) const
{
    std::uint64_t found = 0;
    for (std::size_t word = 0; word < words_.size(); ++word)
    {
        if (StartsWord(text, words_[word]))
        {
            found |= std::uint64_t(1) << word;
        }
    }
    return found;
}

std::uint64_t Search::All() const
{
    return words_.size() == 64 ? ~std::uint64_t(0) : (std::uint64_t(1) << words_.size()) - 1;
}

// As the app's FileName.Normalize: decomposed, without nonspacing marks, each
// character in invariant lower case, and composed again. Most torrent paths
// are ASCII, which needs none of the Windows calls.
std::wstring Search::Normalize(std::string_view text)
{
    if (std::all_of(text.begin(), text.end(), [](char character) { return static_cast<unsigned char>(character) < 0x80; }))
    {
        std::wstring lower(text.size(), L'\0');
        std::transform(text.begin(), text.end(), lower.begin(), [](char character)
        {
            return static_cast<wchar_t>(character >= 'A' && character <= 'Z' ? character - 'A' + 'a' : character);
        });
        return lower;
    }
    auto decomposed = Normalized(Wide(std::string(text)), NormalizationD);
    std::wstring folded;
    for (auto character : decomposed)
    {
        WORD type = 0;
        if (!GetStringTypeW(CT_CTYPE3, &character, 1, &type) || !(type & C3_NONSPACING))
        {
            folded.push_back(character);
        }
    }
    if (folded.empty())
    {
        return folded;
    }
    auto length = static_cast<int>(folded.size());
    auto size = LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_LOWERCASE, folded.c_str(), length, nullptr, 0,
        nullptr, nullptr, 0);
    std::wstring lower(static_cast<std::size_t>(size), L'\0');
    if (size <= 0 || !LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_LOWERCASE, folded.c_str(), length, lower.data(),
        size, nullptr, nullptr, 0))
    {
        throw std::runtime_error("The search text could not be lowered.");
    }
    return Normalized(lower, NormalizationC);
}

Tree::Tree(Listing listing) : listing_(std::move(listing))
{
    auto const& storage = listing_.metadata->layout();
    nodes_.emplace_back();
    paths_.emplace("", 0);
    folderOf_.assign(storage.num_files(), -1);
    // A store that saved a wrong count still lists every file.
    listing_.priorities.resize(storage.num_files(), lt::dont_download);
    if (listing_.progress && *listing_.progress && (*listing_.progress)->size() != folderOf_.size())
    {
        listing_.progress->reset();
    }
    auto const* progress = listing_.progress && *listing_.progress ? &**listing_.progress : nullptr;
    // Files of one folder usually follow each other, so the last folder's
    // path answers most lookups.
    std::optional<std::string> last;
    int folder = 0;
    for (auto index : storage.file_range())
    {
        if (storage.pad_file_at(index))
        {
            continue;
        }
        auto path = ContentPath(storage, index, listing_.layout);
        auto cut = path.rfind('/');
        auto parent = cut == std::string::npos ? std::string_view() : std::string_view(path).substr(0, cut);
        if (last != parent)
        {
            folder = Ensure(parent);
            last = parent;
        }
        auto item = static_cast<int>(index);
        folderOf_[item] = folder;
        nodes_[folder].files.push_back(index);
        nodes_[folder].names += Name(index).size();
        auto size = storage.file_size(index);
        auto priority = listing_.priorities[item];
        for (auto at = folder; at >= 0; at = nodes_[at].parent)
        {
            auto& node = nodes_[at];
            node.mixed |= node.count > 0 && node.priority != priority;
            node.priority = priority;
            ++node.count;
            node.size += size;
            if (priority != lt::dont_download)
            {
                ++node.wantedCount;
                node.wantedSize += size;
            }
            if (progress)
            {
                node.downloaded += (*progress)[item];
            }
        }
    }
}

int Tree::Ensure(std::string_view path)
{
    if (auto found = paths_.find(std::string(path)); found != paths_.end())
    {
        return found->second;
    }
    auto cut = path.rfind('/');
    auto parent = Ensure(cut == std::string_view::npos ? std::string_view() : path.substr(0, cut));
    auto id = static_cast<int>(nodes_.size());
    auto& node = nodes_.emplace_back();
    node.name = cut == std::string_view::npos ? path : path.substr(cut + 1);
    node.parent = parent;
    nodes_[parent].folders.push_back(id);
    nodes_[parent].names += nodes_[id].name.size();
    paths_.emplace(std::string(path), id);
    return id;
}

std::optional<int> Tree::Find(std::string const& path) const
{
    auto found = paths_.find(path);
    return found == paths_.end() ? std::nullopt : std::optional(found->second);
}

std::string_view Tree::Name(lt::file_index_t file) const
{
    return listing_.metadata->layout().file_name(file);
}

bool Tree::TooMany(int folder) const
{
    auto const& node = nodes_[folder];
    return node.folders.size() + node.files.size() > fileWindow || node.names > filePageNames;
}

void Tree::Open(std::vector<int> level, Shown& shown) const
{
    std::erase_if(level, [this](int folder) { return TooMany(folder); });
    while (!level.empty())
    {
        std::size_t entries = 0;
        std::size_t names = 0;
        for (auto folder : level)
        {
            entries += nodes_[folder].folders.size() + nodes_[folder].files.size();
            names += nodes_[folder].names;
        }
        if (shown.entries + entries > fileWindow || shown.names + names > filePageNames)
        {
            return;
        }
        shown.entries += entries;
        shown.names += names;
        std::vector<int> next;
        for (auto folder : level)
        {
            shown.open[folder] = true;
            for (auto child : nodes_[folder].folders)
            {
                if (!TooMany(child))
                {
                    next.push_back(child);
                }
            }
        }
        level = std::move(next);
    }
}

Tree::Matches Tree::Match(Search const& search) const
{
    // Nodes come after their parents, so one pass sees each parent first.
    Matches matches{std::vector<std::uint64_t>(nodes_.size()), std::vector<int>(nodes_.size(), -1)};
    for (std::size_t folder = 1; folder < nodes_.size(); ++folder)
    {
        auto parent = nodes_[folder].parent;
        matches.words[folder] = matches.words[parent] | search.Found(Search::Normalize(nodes_[folder].name));
        matches.top[folder] = matches.top[parent] >= 0 ? matches.top[parent] :
            matches.words[folder] == search.All() ? static_cast<int>(folder) : -1;
    }
    return matches;
}

// A query word never holds a `/`, so the words of a file's path are those of
// its folder's path and those of its name.
bool Tree::Matched(Search const& search, Matches const& matches, lt::file_index_t file) const
{
    auto folder = folderOf_[static_cast<int>(file)];
    return folder >= 0 && (matches.top[folder] >= 0 ||
        (matches.words[folder] | search.Found(Search::Normalize(Name(file)))) == search.All());
}

std::optional<Json> Tree::Describe(Target const& target) const
{
    Shown shown{std::vector<bool>(nodes_.size())};
    Json data = Totals();
    data["path"] = "";
    int folder = 0;
    if (auto const* file = std::get_if<lt::file_index_t>(&target))
    {
        auto item = static_cast<int>(*file);
        if (item < 0 || item >= static_cast<int>(folderOf_.size()) || folderOf_[item] < 0)
        {
            return std::nullopt;
        }
        std::vector<int> chain;
        for (auto at = folderOf_[item]; at >= 0; at = nodes_[at].parent)
        {
            chain.push_back(at);
        }
        for (auto at = chain.rbegin(); at != chain.rend(); ++at)
        {
            auto const& node = nodes_[*at];
            auto entries = node.folders.size() + node.files.size();
            if (TooMany(*at) || shown.entries + entries > fileWindow || shown.names + node.names > filePageNames)
            {
                break;
            }
            shown.open[*at] = true;
            shown.entries += entries;
            shown.names += node.names;
        }
    }
    else if (auto const* named = std::get_if<Folder>(&target))
    {
        auto found = Find(named->path);
        if (!found)
        {
            return std::nullopt;
        }
        folder = *found;
        data["path"] = named->path;
        Open({folder}, shown);
    }
    else
    {
        auto const& search = std::get<Search>(target);
        auto matches = Match(search);
        shown.matches.assign(nodes_.size() + folderOf_.size(), false);
        // The root lists its matches, also when there are none.
        shown.folders[0];
        shown.files[0];
        // A folder and its ancestors are listed once they hold an entry of
        // the reply; the root is the reply's folder itself.
        std::vector<bool> listed(nodes_.size());
        listed[0] = true;
        std::vector<bool> counted(nodes_.size());
        std::int64_t count = 0;
        std::int64_t wanted = 0;
        std::vector<int> matched;
        bool full = false;
        auto show = [&](int parent, std::size_t name)
        {
            std::size_t entries = 1;
            std::size_t names = name;
            for (auto at = parent; !listed[at]; at = nodes_[at].parent)
            {
                ++entries;
                names += nodes_[at].name.size();
            }
            if (full || shown.entries + entries > fileWindow || shown.names + names > filePageNames)
            {
                full = true;
                return false;
            }
            shown.entries += entries;
            shown.names += names;
            for (auto at = parent; !listed[at]; at = nodes_[at].parent)
            {
                listed[at] = true;
                shown.folders[nodes_[at].parent].push_back(at);
            }
            return true;
        };
        for (auto file : listing_.metadata->layout().file_range())
        {
            auto item = static_cast<int>(file);
            auto parent = folderOf_[item];
            if (parent < 0)
            {
                continue;
            }
            if (auto top = matches.top[parent]; top >= 0)
            {
                if (counted[top])
                {
                    continue;
                }
                counted[top] = true;
                count += nodes_[top].count;
                wanted += nodes_[top].wantedCount;
                if (show(nodes_[top].parent, nodes_[top].name.size()))
                {
                    listed[top] = true;
                    shown.folders[nodes_[top].parent].push_back(top);
                    shown.matches[top] = true;
                    matched.push_back(top);
                }
            }
            else if (Matched(search, matches, file))
            {
                ++count;
                wanted += listing_.priorities[item] != lt::dont_download;
                if (show(parent, Name(file).size()))
                {
                    shown.files[parent].push_back(file);
                    shown.matches[nodes_.size() + item] = true;
                }
            }
        }
        Open(std::move(matched), shown);
        data["match_count"] = count;
        data["match_wanted"] = wanted;
    }
    data["folder"] = Describe(folder, shown);
    return data;
}

std::optional<std::vector<lt::file_index_t>> Tree::Files(Target const& target) const
{
    std::vector<lt::file_index_t> files;
    if (auto const* file = std::get_if<lt::file_index_t>(&target))
    {
        if (static_cast<int>(*file) < 0 || static_cast<int>(*file) >= static_cast<int>(folderOf_.size()))
        {
            return std::nullopt;
        }
        files.push_back(*file);
    }
    else if (auto const* named = std::get_if<Folder>(&target))
    {
        auto found = Find(named->path);
        if (!found)
        {
            return std::nullopt;
        }
        Collect(*found, files);
    }
    else
    {
        auto const& search = std::get<Search>(target);
        auto matches = Match(search);
        for (auto file : listing_.metadata->layout().file_range())
        {
            if (Matched(search, matches, file))
            {
                files.push_back(file);
            }
        }
    }
    return files;
}

Json Tree::Values(std::vector<std::string> const& paths) const
{
    Json values = Json::array();
    for (auto const& path : paths)
    {
        if (auto found = Find(path))
        {
            Json value = {{"path", path}};
            Values(nodes_[*found], value);
            values.push_back(std::move(value));
        }
    }
    return values;
}

Json Tree::Totals() const
{
    return {{"wanted_count", nodes_[0].wantedCount}, {"wanted_size", nodes_[0].wantedSize}};
}

Json Tree::Describe(int folder, Shown const& shown) const
{
    auto const& node = nodes_[folder];
    Json value = {{"name", node.name}};
    Values(node, value);
    value["too_many"] = TooMany(folder);
    value["match"] = !shown.matches.empty() && shown.matches[folder];
    value["folders"] = nullptr;
    value["files"] = nullptr;
    std::vector<int> const* children = nullptr;
    std::vector<lt::file_index_t> const* leaves = nullptr;
    if (shown.open[folder])
    {
        children = &node.folders;
        leaves = &node.files;
    }
    if (auto found = shown.folders.find(folder); found != shown.folders.end() && !children)
    {
        children = &found->second;
    }
    if (auto found = shown.files.find(folder); found != shown.files.end() && !leaves)
    {
        leaves = &found->second;
    }
    if (!children && !leaves)
    {
        return value;
    }
    static std::vector<int> const noFolders;
    static std::vector<lt::file_index_t> const noFiles;
    value["folders"] = Json::array();
    for (auto child : children ? *children : noFolders)
    {
        value["folders"].push_back(Describe(child, shown));
    }
    value["files"] = Json::array();
    for (auto file : leaves ? *leaves : noFiles)
    {
        value["files"].push_back(DescribeFile(file,
            !shown.matches.empty() && shown.matches[nodes_.size() + static_cast<int>(file)]));
    }
    return value;
}

Json Tree::DescribeFile(lt::file_index_t file, bool match) const
{
    auto item = static_cast<int>(file);
    auto pieces = Pieces(*listing_.metadata, file);
    Json value = {{"index", item}, {"name", Name(file)}, {"size", listing_.metadata->layout().file_size(file)},
        {"priority", static_cast<std::uint8_t>(listing_.priorities[item])}, {"first_piece", pieces.first},
        {"end_piece", pieces.end}, {"match", match}};
    if (listing_.progress)
    {
        value["downloaded"] = *listing_.progress ? Json((**listing_.progress)[item]) : Json();
        value["disk_path"] = nullptr;
    }
    return value;
}

void Tree::Values(Node const& node, Json& value) const
{
    value["file_count"] = node.count;
    value["size"] = node.size;
    value["wanted_count"] = node.wantedCount;
    value["wanted_size"] = node.wantedSize;
    value["priority"] = node.mixed ? Json() : Json(static_cast<std::uint8_t>(node.priority));
    if (listing_.progress)
    {
        value["downloaded"] = *listing_.progress ? Json(node.downloaded) : Json();
    }
}

void Tree::Collect(int folder, std::vector<lt::file_index_t>& files) const
{
    auto const& node = nodes_[folder];
    files.insert(files.end(), node.files.begin(), node.files.end());
    for (auto child : node.folders)
    {
        Collect(child, files);
    }
}
}
