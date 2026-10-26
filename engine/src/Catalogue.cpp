#include "Catalogue.h"
#include "Engine.h"
#include <Windows.h>
#include <libtorrent/hasher.hpp>
#include <libtorrent/hex.hpp>
#include <libtorrent/load_torrent.hpp>
#include <libtorrent/read_resume_data.hpp>
#include <libtorrent/write_resume_data.hpp>
#include <algorithm>
#include <atomic>
#include <charconv>
#include <chrono>
#include <cmath>
#include <map>
#include <set>
#include <stdexcept>
#include <string_view>
#include <tuple>
#include <utility>

namespace tt
{
namespace
{
struct sqlite3;
struct sqlite3_stmt;
using Destructor = void(*)(void*);
constexpr int row = 100;
constexpr int done = 101;
constexpr int sqlNull = 5;
constexpr int schemaVersion = 4;
constexpr std::size_t visitLimit = 20'000;
constexpr int sourceLimit = 32;
constexpr auto scanTime = std::chrono::milliseconds(75);

class Interrupted : public std::runtime_error
{
public:
    Interrupted() : std::runtime_error("Catalogue operation interrupted") {}
};

std::int64_t Now()
{
    return std::chrono::duration_cast<std::chrono::seconds>(
        std::chrono::system_clock::now().time_since_epoch()).count();
}

class Sqlite
{
public:
    explicit Sqlite(std::atomic<CatalogueCondition>& condition) : condition(condition)
    {
        auto path = std::filesystem::path(Executable()).parent_path() / L"e_sqlite3.dll";
        module = LoadLibraryExW(path.c_str(), nullptr,
            LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!module)
            throw std::runtime_error("Bundled SQLite runtime is unavailable");
        try
        {
#define TT_SQLITE(name) Load(name, "sqlite3_" #name)
            TT_SQLITE(open_v2); TT_SQLITE(close_v2); TT_SQLITE(errmsg);
            TT_SQLITE(prepare_v2); TT_SQLITE(finalize); TT_SQLITE(step);
            TT_SQLITE(reset); TT_SQLITE(clear_bindings); TT_SQLITE(bind_text);
            TT_SQLITE(bind_blob); TT_SQLITE(bind_int64); TT_SQLITE(bind_null);
            TT_SQLITE(column_text); TT_SQLITE(column_blob); TT_SQLITE(column_bytes);
            TT_SQLITE(column_int64); TT_SQLITE(column_type); TT_SQLITE(exec);
            TT_SQLITE(last_insert_rowid); TT_SQLITE(changes); TT_SQLITE(interrupt);
            TT_SQLITE(busy_timeout); TT_SQLITE(progress_handler);
#undef TT_SQLITE
        }
        catch (...)
        {
            FreeLibrary(module);
            throw;
        }
    }
    ~Sqlite() { FreeLibrary(module); }
    Sqlite(Sqlite const&) = delete;
    Sqlite& operator=(Sqlite const&) = delete;

    int (*open_v2)(char const*, sqlite3**, int, char const*) = nullptr;
    int (*close_v2)(sqlite3*) = nullptr;
    char const* (*errmsg)(sqlite3*) = nullptr;
    int (*prepare_v2)(sqlite3*, char const*, int, sqlite3_stmt**, char const**) = nullptr;
    int (*finalize)(sqlite3_stmt*) = nullptr;
    int (*step)(sqlite3_stmt*) = nullptr;
    int (*reset)(sqlite3_stmt*) = nullptr;
    int (*clear_bindings)(sqlite3_stmt*) = nullptr;
    int (*bind_text)(sqlite3_stmt*, int, char const*, int, Destructor) = nullptr;
    int (*bind_blob)(sqlite3_stmt*, int, void const*, int, Destructor) = nullptr;
    int (*bind_int64)(sqlite3_stmt*, int, std::int64_t) = nullptr;
    int (*bind_null)(sqlite3_stmt*, int) = nullptr;
    unsigned char const* (*column_text)(sqlite3_stmt*, int) = nullptr;
    void const* (*column_blob)(sqlite3_stmt*, int) = nullptr;
    int (*column_bytes)(sqlite3_stmt*, int) = nullptr;
    std::int64_t (*column_int64)(sqlite3_stmt*, int) = nullptr;
    int (*column_type)(sqlite3_stmt*, int) = nullptr;
    int (*exec)(sqlite3*, char const*, int(*)(void*, int, char**, char**), void*, char**) = nullptr;
    std::int64_t (*last_insert_rowid)(sqlite3*) = nullptr;
    int (*changes)(sqlite3*) = nullptr;
    void (*interrupt)(sqlite3*) = nullptr;
    int (*busy_timeout)(sqlite3*, int) = nullptr;
    void (*progress_handler)(sqlite3*, int, int(*)(void*), void*) = nullptr;
    [[noreturn]] void Fail(sqlite3* database, int result)
    {
        if (result == 9) throw Interrupted();
        auto primary = result & 0xff;
        condition = primary == 11 || primary == 26 ? CatalogueCondition::Damaged : CatalogueCondition::Unavailable;
        throw std::runtime_error(errmsg(database));
    }

private:
    HMODULE module = nullptr;
    std::atomic<CatalogueCondition>& condition;
    template <typename Function>
    void Load(Function& target, char const* name)
    {
        target = reinterpret_cast<Function>(GetProcAddress(module, name));
        if (!target)
            throw std::runtime_error("Bundled SQLite export is unavailable");
    }
};

class Statement
{
public:
    Statement(Sqlite& api, sqlite3* database, std::string const& sql) : api(api), database(database)
    {
        Check(api.prepare_v2(database, sql.c_str(), static_cast<int>(sql.size()), &value, nullptr));
    }
    ~Statement() { api.finalize(value); }
    Statement(Statement const&) = delete;
    Statement& operator=(Statement const&) = delete;
    void Text(int index, std::string const& text)
    {
        Check(api.bind_text(value, index, text.data(), static_cast<int>(text.size()),
            reinterpret_cast<Destructor>(-1)));
    }
    void Blob(int index, char const* bytes, std::size_t size)
    {
        Check(api.bind_blob(value, index, bytes ? bytes : "", static_cast<int>(size),
            reinterpret_cast<Destructor>(-1)));
    }
    void Number(int index, std::int64_t number) { Check(api.bind_int64(value, index, number)); }
    void Null(int index) { Check(api.bind_null(value, index)); }
    void Optional(int index, std::optional<std::int64_t> number)
    {
        if (number) Number(index, *number); else Null(index);
    }
    bool Step()
    {
        auto result = api.step(value);
        if (result == row) return true;
        if (result == done) return false;
        Check(result);
        return false;
    }
    void Reset()
    {
        Check(api.reset(value));
        Check(api.clear_bindings(value));
    }
    std::string Text(int column) const
    {
        auto bytes = api.column_text(value, column);
        return bytes ? std::string(reinterpret_cast<char const*>(bytes), api.column_bytes(value, column)) : "";
    }
    std::vector<char> Blob(int column) const
    {
        auto bytes = static_cast<char const*>(api.column_blob(value, column));
        return bytes ? std::vector<char>(bytes, bytes + api.column_bytes(value, column)) : std::vector<char>{};
    }
    std::int64_t Number(int column) const { return api.column_int64(value, column); }
    std::optional<std::int64_t> Optional(int column) const
    {
        return api.column_type(value, column) == sqlNull ? std::nullopt : std::optional(Number(column));
    }

private:
    Sqlite& api;
    sqlite3* database;
    sqlite3_stmt* value = nullptr;
    void Check(int result) const
    {
        if (result != 0) api.Fail(database, result);
    }
};

class ScanBudget
{
public:
    ScanBudget(Sqlite& api, sqlite3* database) : api(api), database(database),
        deadline(std::chrono::steady_clock::now() + scanTime)
    {
        api.progress_handler(database, 1000, [](void* context)
        {
            return std::chrono::steady_clock::now() >= static_cast<ScanBudget*>(context)->deadline ? 1 : 0;
        }, this);
    }
    ~ScanBudget() { api.progress_handler(database, 0, nullptr, nullptr); }
private:
    Sqlite& api;
    sqlite3* database;
    std::chrono::steady_clock::time_point deadline;
};

std::wstring Fold(std::string const& value)
{
    auto text = Wide(value);
    std::replace(text.begin(), text.end(), L'\\', L'/');
    auto length = FoldStringW(MAP_PRECOMPOSED, text.data(), static_cast<int>(text.size()), nullptr, 0);
    if (length > 0)
    {
        std::wstring composed(length, L'\0');
        FoldStringW(MAP_PRECOMPOSED, text.data(), static_cast<int>(text.size()), composed.data(), length);
        text = std::move(composed);
    }
    length = LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_LOWERCASE, text.data(),
        static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr, 0);
    if (length > 0)
    {
        std::wstring lower(length, L'\0');
        LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_LOWERCASE, text.data(),
            static_cast<int>(text.size()), lower.data(), length, nullptr, nullptr, 0);
        return lower;
    }
    return text;
}

bool Matches(std::wstring const& text, std::wstring const& pattern)
{
    if (pattern.find_first_of(L"*?") == std::wstring::npos)
        return text.find(pattern) != std::wstring::npos;
    std::size_t at = 0, part = 0, star = std::wstring::npos, retry = 0;
    while (at < text.size())
    {
        if (part < pattern.size() && (pattern[part] == L'?' || pattern[part] == text[at]))
        {
            ++part; ++at;
        }
        else if (part < pattern.size() && pattern[part] == L'*')
        {
            star = part++; retry = at;
        }
        else if (star != std::wstring::npos)
        {
            part = star + 1; at = ++retry;
        }
        else return false;
    }
    while (part < pattern.size() && pattern[part] == L'*') ++part;
    return part == pattern.size();
}

bool InRange(std::int64_t value, Catalogue::Range const& range)
{
    return (!range.minimum || value >= *range.minimum) && (!range.maximum || value <= *range.maximum);
}

bool HasRange(Catalogue::Range const& range) { return range.minimum || range.maximum; }

std::string Extension(std::string const& name)
{
    auto dot = name.find_last_of('.');
    return dot == std::string::npos ? "" : name.substr(dot + 1);
}

bool TextMatch(std::string const& value, Catalogue::Text const& condition)
{
    if (condition.value.empty()) return true;
    auto match = Matches(Fold(value), Fold(condition.value));
    return condition.exclude ? !match : match;
}

bool HasExtension(std::wstring const& filename, std::wstring const& pattern)
{
    return pattern.find_first_of(L"*?") == std::wstring::npos ?
        filename.ends_with(L"." + pattern) : Matches(filename, L"*." + pattern);
}

bool Extensions(std::string const& filename, Catalogue::Text const& condition)
{
    if (condition.value.empty()) return true;
    bool match = false;
    auto name = Fold(filename);
    auto patterns = Fold(condition.value);
    std::size_t begin = 0;
    while (begin < patterns.size())
    {
        auto end = patterns.find_first_of(L" ,;\r\n\t", begin);
        if (end == std::wstring::npos) end = patterns.size();
        auto pattern = patterns.substr(begin, end - begin);
        if (pattern.starts_with(L'.')) pattern.erase(0, 1);
        if (!pattern.empty() && HasExtension(name, pattern)) match = true;
        begin = end + 1;
    }
    return condition.exclude ? !match : match;
}

std::string Fingerprint(Catalogue::Rule const& rule)
{
    auto text = [](Catalogue::Text const& value) { return Json::array({value.value, value.exclude}); };
    auto range = [](Catalogue::Range const& value)
    {
        return Json::array({value.minimum ? Json(*value.minimum) : Json(),
            value.maximum ? Json(*value.maximum) : Json()});
    };
    auto bytes = Json::array({rule.ruleId, text(rule.title), range(rule.size), range(rule.count),
        static_cast<int>(rule.quantifier), text(rule.filename), text(rule.path),
        text(rule.extension), range(rule.fileSize)}).dump();
    auto hash = libtorrent::hasher256(bytes.data(), static_cast<int>(bytes.size())).final();
    return libtorrent::aux::to_hex({hash.data(), 32});
}

bool Rejects(Catalogue::Rule const& rule, std::string const& name,
    std::int64_t size, int count, std::vector<Catalogue::File> const& files)
{
    if (!rule.enabled || !TextMatch(name, rule.title) || !InRange(size, rule.size) || !InRange(count, rule.count))
        return false;
    bool fileCondition = !rule.filename.value.empty() || !rule.path.value.empty() ||
        !rule.extension.value.empty() || HasRange(rule.fileSize);
    if (!fileCondition) return true;
    int tested = 0, matched = 0;
    for (auto const& file : files)
    {
        if (file.padding) continue;
        ++tested;
        if (TextMatch(file.name, rule.filename) && TextMatch(file.path, rule.path) &&
            Extensions(file.name, rule.extension) && InRange(file.size, rule.fileSize)) ++matched;
    }
    switch (rule.quantifier)
    {
    case FileQuantifier::Any: return matched > 0;
    case FileQuantifier::Every: return tested > 0 && matched == tested;
    case FileQuantifier::None: return matched == 0;
    }
    return false;
}

std::vector<Catalogue::File> Files(libtorrent::torrent_info const& metadata)
{
    auto const& layout = metadata.layout();
    std::vector<Catalogue::File> result;
    result.reserve(layout.num_files());
    for (int index = 0; index < layout.num_files(); ++index)
    {
        auto id = libtorrent::file_index_t(index);
        auto path = layout.file_path(id);
        std::replace(path.begin(), path.end(), '\\', '/');
        auto slash = path.find_last_of('/');
        auto root = layout.root(id);
        result.push_back({index, path.substr(slash == std::string::npos ? 0 : slash + 1), path,
            layout.file_size(id), bool(layout.file_flags(id) & libtorrent::file_storage::flag_pad_file),
            bool(layout.file_flags(id) & libtorrent::file_storage::flag_symlink),
            root.is_all_zeros() ? "" : libtorrent::aux::to_hex({root.data(), 32})});
    }
    return result;
}

bool CompleteLayers(libtorrent::add_torrent_params const& params)
{
    if (!params.ti || !params.ti->info_hashes().has_v2() || params.merkle_trees.empty()) return false;
    try
    {
        auto bytes = libtorrent::write_torrent_file_buf(params, {});
        libtorrent::load_torrent_limits limits;
        limits.max_buffer_size = static_cast<int>(bytes.size());
        limits.max_decode_depth = 200;
        libtorrent::load_torrent_buffer(libtorrent::span<char const>(bytes), limits);
        return true;
    }
    catch (libtorrent::system_error const& error)
    {
        if (error.code() == libtorrent::errors::torrent_missing_piece_layer ||
            error.code() == libtorrent::errors::torrent_invalid_piece_layer) return false;
        throw;
    }
}

std::int64_t ParseSize(std::string value)
{
    auto text = Fold(value);
    std::size_t end = 0;
    while (end < text.size() && ((text[end] >= L'0' && text[end] <= L'9') || text[end] == L'.')) ++end;
    if (end == 0) throw std::invalid_argument("Invalid size filter");
    auto number = std::stod(Utf8(text.substr(0, end)));
    auto unit = text.substr(end);
    double multiplier = 1;
    if (unit == L"kib") multiplier = 1024;
    else if (unit == L"mib") multiplier = 1024 * 1024;
    else if (unit == L"gib") multiplier = 1024.0 * 1024 * 1024;
    else if (unit == L"tib") multiplier = 1024.0 * 1024 * 1024 * 1024;
    else if (unit == L"kb" || unit == L"k") multiplier = 1000;
    else if (unit == L"mb" || unit == L"m") multiplier = 1000 * 1000;
    else if (unit == L"gb" || unit == L"g") multiplier = 1000.0 * 1000 * 1000;
    else if (unit != L"" && unit != L"b") throw std::invalid_argument("Invalid size unit");
    number *= multiplier;
    if (!std::isfinite(number) || number < 0 || number >= static_cast<double>(INT64_MAX))
        throw std::invalid_argument("Invalid size range");
    return static_cast<std::int64_t>(number);
}

Catalogue::Range SizeRange(std::string value)
{
    Catalogue::Range result;
    auto separator = value.find("..");
    if (separator != std::string::npos)
    {
        result.minimum = ParseSize(value.substr(0, separator));
        result.maximum = ParseSize(value.substr(separator + 2));
    }
    else if (value.starts_with(">=")) result.minimum = ParseSize(value.substr(2));
    else if (value.starts_with("<=")) result.maximum = ParseSize(value.substr(2));
    else if (value.starts_with('>')) result.minimum = ParseSize(value.substr(1)) + 1;
    else if (value.starts_with('<')) result.maximum = ParseSize(value.substr(1)) - 1;
    else
    {
        if (value.starts_with('=')) value.erase(0, 1);
        result.minimum = result.maximum = ParseSize(value);
    }
    if (result.minimum && result.maximum && result.minimum > result.maximum)
        throw std::invalid_argument("Reversed size range");
    return result;
}

class Predicate
{
public:
    struct Atom
    {
        std::string field;
        std::wstring pattern;
        Catalogue::Range range;
        bool excludes = false;
    };
    explicit Predicate(Catalogue::Query const& query)
    {
        std::vector<std::string> tokens;
        std::string token;
        bool quoted = false;
        for (char character : query.text)
        {
            if (character == '"') quoted = !quoted;
            else if (!quoted && (character == '|' || character == ' ' || character == '\t'))
            {
                if (!token.empty()) { tokens.push_back(std::move(token)); token.clear(); }
                if (character == '|') tokens.push_back("|");
            }
            else token += character;
        }
        if (quoted) throw std::invalid_argument("Unclosed search quote");
        if (!token.empty()) tokens.push_back(std::move(token));
        bool alternative = false;
        for (auto value : tokens)
        {
            if (value == "|")
            {
                if (groups.empty() || alternative) throw std::invalid_argument("Incomplete search alternative");
                alternative = true;
                continue;
            }
            Atom atom;
            if (value.starts_with('!')) { atom.excludes = true; value.erase(0, 1); }
            if (value.empty()) throw std::invalid_argument("Incomplete search exclusion");
            auto colon = value.find(':');
            if (colon != std::string::npos)
            {
                auto field = Utf8(Fold(value.substr(0, colon)));
                if (field == "ext" || field == "path" || field == "torrent" || field == "size")
                {
                    atom.field = field;
                    value.erase(0, colon + 1);
                }
            }
            if (value.empty()) throw std::invalid_argument("Missing search value");
            if (atom.field == "size") atom.range = SizeRange(value);
            else atom.pattern = Fold(value);
            if (atom.field == "ext" && atom.pattern.starts_with(L"*.")) atom.pattern.erase(0, 2);
            else if (atom.field == "ext" && atom.pattern.starts_with(L'.')) atom.pattern.erase(0, 1);
            if (!alternative) groups.emplace_back();
            groups.back().push_back(std::move(atom));
            alternative = false;
        }
        if (alternative) throw std::invalid_argument("Incomplete search alternative");
        extension = {query.extension, false};
        size = query.size;
        torrent = Fold(query.torrent);
    }
    bool Matches(Catalogue::Source const& source, bool file) const
    {
        if (!torrent.empty() && !tt::Matches(Fold(source.name), torrent) &&
            Fold(source.hash) != torrent && (!source.v1 || Fold(*source.v1) != torrent) &&
            (!source.v2 || Fold(*source.v2) != torrent)) return false;
        if (!InRange(file ? source.file.size : source.size, size)) return false;
        if (file && !Extensions(source.file.name, extension)) return false;
        for (auto const& group : groups)
        {
            bool matched = false;
            for (auto const& atom : group)
            {
                bool value;
                if (atom.field == "size") value = InRange(file ? source.file.size : source.size, atom.range);
                else
                {
                    std::wstring text;
                    if (atom.field == "torrent") text = Fold(source.name);
                    else if (atom.field == "ext") text = Fold(file ? Extension(source.file.name) : "");
                    else text = Fold(file ? source.file.path : source.name);
                    value = tt::Matches(text, atom.pattern);
                    if (atom.field == "ext" && file)
                        value = HasExtension(Fold(source.file.name), atom.pattern);
                    if (atom.field == "torrent")
                        value |= Fold(source.hash) == atom.pattern || (source.v1 && Fold(*source.v1) == atom.pattern) ||
                            (source.v2 && Fold(*source.v2) == atom.pattern);
                }
                matched |= atom.excludes ? !value : value;
            }
            if (!matched) return false;
        }
        return true;
    }
    std::string Candidate() const
    {
        std::string result;
        for (auto const& group : groups)
        {
            std::string alternatives;
            bool usable = true;
            for (auto const& atom : group)
            {
                if (atom.excludes || atom.field == "torrent" || atom.field == "size") { usable = false; break; }
                std::wstring literal, longest;
                for (auto character : atom.pattern)
                {
                    if (character == L'*' || character == L'?')
                    {
                        if (literal.size() > longest.size()) longest = literal;
                        literal.clear();
                    }
                    else literal += character;
                }
                if (literal.size() > longest.size()) longest = literal;
                auto text = Utf8(longest);
                if (std::count_if(text.begin(), text.end(), [](unsigned char byte) { return (byte & 0xc0) != 0x80; }) < 3)
                { usable = false; break; }
                if (!alternatives.empty()) alternatives += " OR ";
                alternatives += '"';
                for (auto character : text) alternatives += character == '"' ? "\"\"" : std::string(1, character);
                alternatives += '"';
            }
            if (!usable || alternatives.empty()) continue;
            if (!result.empty()) result += " AND ";
            result += '(' + alternatives + ')';
        }
        return result;
    }
    bool Parent(Catalogue::Source const& source) const
    {
        if (!torrent.empty() && !tt::Matches(Fold(source.name), torrent) && Fold(source.hash) != torrent &&
            (!source.v1 || Fold(*source.v1) != torrent) && (!source.v2 || Fold(*source.v2) != torrent)) return false;
        for (auto const& group : groups)
        {
            bool matched = false;
            for (auto const& atom : group)
            {
                if (atom.field != "torrent") { matched = true; break; }
                bool value = tt::Matches(Fold(source.name), atom.pattern) || Fold(source.hash) == atom.pattern ||
                    (source.v1 && Fold(*source.v1) == atom.pattern) || (source.v2 && Fold(*source.v2) == atom.pattern);
                matched |= atom.excludes ? !value : value;
            }
            if (!matched) return false;
        }
        return true;
    }
private:
    std::vector<std::vector<Atom>> groups;
    Catalogue::Text extension;
    Catalogue::Range size;
    std::wstring torrent;
};
}

class Catalogue::Impl
{
public:
    explicit Impl(std::filesystem::path directory) : path(std::move(directory) / L"index.db") {}
    ~Impl()
    {
        interruptible = nullptr;
        auto current = database.exchange(nullptr);
        if (current) api->close_v2(current);
    }
    std::filesystem::path path;
    std::unique_ptr<Sqlite> api;
    std::atomic<CatalogueCondition> condition{CatalogueCondition::Healthy};
    std::atomic<sqlite3*> database{nullptr};
    std::atomic<sqlite3*> interruptible{nullptr};
    Settings settings;
    std::map<std::string, std::string> active;
    std::string probeSource;
    bool preferSource = false;
    struct Review
    {
        CatalogueRemoval kind;
        std::int64_t watermark;
        std::int64_t generation;
        std::int64_t cursor = 0;
        std::int64_t applied = 0;
        bool complete = false;
        Removal result;
    };
    std::optional<Review> review;
    std::uint64_t reviewSequence = 0;
    struct Projection
    {
        std::int64_t entry;
        std::vector<File> files;
    };
    std::optional<Projection> projection;
    struct SearchGroup
    {
        std::optional<Cursor> after;
        std::optional<std::int64_t> matched;
        std::optional<std::int64_t> preferred;
        Hit hit;
        bool complete = false;
    };
    std::string searchKey;
    std::map<std::pair<std::string, std::int64_t>, SearchGroup> searchGroups;

    Statement Prepare(std::string const& sql)
    {
        if (!database) throw std::runtime_error("Catalogue has not been created");
        return Statement(*api, database, sql);
    }
    void Execute(std::string const& sql)
    {
        auto result = api->exec(database, sql.c_str(), nullptr, nullptr, nullptr);
        if (result != 0) api->Fail(database, result);
    }
    std::int64_t Scalar(std::string const& sql)
    {
        auto query = Prepare(sql);
        return query.Step() ? query.Number(0) : 0;
    }
    Progress ReadProgress()
    {
        if (!database) return {};
        return {Scalar("SELECT rebuild_active FROM state WHERE state_id=1") != 0,
            Scalar("SELECT unpublished>0 FROM totals WHERE state_id=1") != 0};
    }
    class Transaction
    {
    public:
        explicit Transaction(Impl& owner) : owner(owner) { owner.Execute("BEGIN IMMEDIATE"); }
        ~Transaction() { if (!committed) owner.api->exec(owner.database, "ROLLBACK", nullptr, nullptr, nullptr); }
        void Commit() { owner.Execute("COMMIT"); committed = true; }
    private:
        Impl& owner;
        bool committed = false;
    };
    std::int64_t Find(std::string const& hash)
    {
        auto query = Prepare("SELECT catalogue_id FROM lookups WHERE lookup=?");
        query.Text(1, hash);
        return query.Step() ? query.Number(0) : 0;
    }
    std::int64_t Generation() { return Scalar("SELECT generation FROM state WHERE state_id=1"); }
    std::pair<std::int64_t, std::int64_t> Footprint()
    {
        auto size = Scalar("PRAGMA page_size");
        auto allocated = Scalar("PRAGMA page_count") * size;
        auto reusable = Scalar("PRAGMA freelist_count") * size;
        for (auto const* suffix : {L"-journal", L"-wal", L"-shm"})
        {
            std::error_code error;
            auto bytes = std::filesystem::file_size(path.wstring() + suffix, error);
            if (!error) allocated += static_cast<std::int64_t>(bytes);
        }
        return {allocated, reusable};
    }
    void Changed()
    {
        Execute("UPDATE state SET generation=generation+1 WHERE state_id=1");
        review.reset();
    }
    void Alias(std::string const& lookup, std::int64_t id)
    {
        if (lookup.empty()) return;
        auto insert = Prepare("INSERT INTO lookups(lookup,catalogue_id) VALUES(?,?) "
            "ON CONFLICT(lookup) DO UPDATE SET catalogue_id=excluded.catalogue_id");
        insert.Text(1, lookup); insert.Number(2, id); insert.Step();
        auto remove = Prepare("DELETE FROM pending WHERE lookup=?");
        remove.Text(1, lookup); remove.Step();
    }
    std::vector<std::pair<std::string, std::string>> Reasons(std::string const& title,
        std::int64_t size, int count, std::vector<File> const& files) const
    {
        std::vector<std::pair<std::string, std::string>> result;
        for (auto const& rule : settings.rules)
            if (Rejects(rule, title, size, count, files))
                result.emplace_back(Fingerprint(rule), rule.name.empty() ? rule.ruleId : rule.name);
        return result;
    }
    void Remember(std::vector<std::string> const& keys,
        std::vector<std::pair<std::string, std::string>> const& reasons)
    {
        if (keys.empty()) return;
        auto identity = keys.front();
        for (auto const& key : keys) if (key.size() == 64) identity = key;
        auto reject = Prepare("INSERT INTO rejections(lookup,identity,decided) VALUES(?,?,?) "
            "ON CONFLICT(lookup) DO UPDATE SET identity=excluded.identity,decided=excluded.decided");
        auto reason = Prepare("INSERT OR REPLACE INTO reasons(lookup,fingerprint,reason) VALUES(?,?,?)");
        auto remove = Prepare("DELETE FROM pending WHERE lookup=?");
        for (auto const& key : keys)
        {
            if (key.empty()) continue;
            reject.Text(1, key); reject.Text(2, identity); reject.Number(3, Now()); reject.Step(); reject.Reset();
            remove.Text(1, key); remove.Step(); remove.Reset();
            for (auto const& [fingerprint, name] : reasons)
            {
                reason.Text(1, key); reason.Text(2, fingerprint); reason.Text(3, name);
                reason.Step(); reason.Reset();
            }
        }
    }
    Source ReadSource(Statement const& query, int start = 0) const
    {
        Source result;
        auto v1 = query.Text(start + 0), v2 = query.Text(start + 1);
        if (!v1.empty()) result.v1 = v1;
        if (!v2.empty()) result.v2 = v2;
        result.hash = v2.empty() ? v1 : v2;
        result.name = query.Text(start + 2);
        result.size = query.Number(start + 3);
        result.count = static_cast<int>(query.Number(start + 4));
        result.admitted = query.Number(start + 5);
        result.seen = query.Optional(start + 6);
        result.availability = {query.Optional(start + 7), query.Optional(start + 8),
            query.Number(start + 9), static_cast<int>(query.Number(start + 10))};
        return result;
    }
    File ReadFile(Statement const& query, int start) const
    {
        return {static_cast<int>(query.Number(start)), query.Text(start + 1), query.Text(start + 2),
            query.Number(start + 3), query.Number(start + 4) != 0, query.Number(start + 5) != 0,
            query.Text(start + 6)};
    }
    static std::string SourceColumns()
    {
        return "c.v1,c.v2,c.name,c.size,c.count,c.admitted,c.seen,c.seeds,c.peers,c.observed,c.responses";
    }
    int Project(std::int64_t id, std::vector<File> const& files, int begin, int limit,
        std::chrono::steady_clock::time_point deadline)
    {
        auto insert = Prepare("INSERT INTO files(catalogue_id,file_index,name,path,size,padding,symlink,root,extension,name_key) "
            "VALUES(?,?,?,?,?,?,?,?,?,?) ON CONFLICT(catalogue_id,file_index) DO UPDATE SET "
            "name=excluded.name,path=excluded.path,size=excluded.size,padding=excluded.padding,symlink=excluded.symlink,"
            "root=excluded.root,extension=excluded.extension,name_key=excluded.name_key");
        auto identity = Prepare("SELECT file_id FROM files WHERE catalogue_id=? AND file_index=?");
        auto removeText = Prepare("DELETE FROM file_search WHERE rowid=?");
        auto text = Prepare("INSERT INTO file_search(rowid,path) VALUES(?,?)");
        auto end = std::min(static_cast<int>(files.size()), begin + std::clamp(limit, 1, 2048));
        int next = begin;
        while (next < end)
        {
            auto const& file = files[next];
            insert.Number(1, id); insert.Number(2, file.index); insert.Text(3, file.name);
            insert.Text(4, file.path); insert.Number(5, file.size); insert.Number(6, file.padding);
            insert.Number(7, file.symlink);
            if (file.root.empty()) insert.Null(8); else insert.Text(8, file.root);
            insert.Text(9, Utf8(Fold(Extension(file.name)))); insert.Text(10, Utf8(Fold(file.name)));
            insert.Step(); insert.Reset();
            identity.Number(1, id); identity.Number(2, file.index); identity.Step();
            auto fileId = identity.Number(0); identity.Reset();
            removeText.Number(1, fileId); removeText.Step(); removeText.Reset();
            text.Number(1, fileId); text.Text(2, Utf8(Fold(file.path))); text.Step(); text.Reset();
            ++next;
            if (std::chrono::steady_clock::now() >= deadline) break;
        }
        return next;
    }
    libtorrent::add_torrent_params Load(std::int64_t id)
    {
        auto query = Prepare("SELECT info,resume,extras FROM metadata WHERE catalogue_id=?");
        query.Number(1, id);
        if (!query.Step()) throw std::runtime_error("Catalogue metadata is unavailable");
        auto bytes = query.Blob(0);
        auto extras = Json::parse(query.Text(2));
        auto resume = query.Blob(1);
        libtorrent::load_torrent_limits limits;
        limits.max_buffer_size = static_cast<int>(resume.size());
        limits.max_decode_depth = 200;
        auto saved = libtorrent::read_resume_data(libtorrent::span<char const>(resume), limits);
        if (!saved.ti || !std::equal(bytes.begin(), bytes.end(), saved.ti->info_section().begin(), saved.ti->info_section().end()))
            throw std::runtime_error("Retained metadata does not match its torrent");
        libtorrent::add_torrent_params params;
        params.ti = std::move(saved.ti);
        params.info_hashes = params.ti->info_hashes();
        params.merkle_trees = std::move(saved.merkle_trees);
        params.merkle_tree_mask = std::move(saved.merkle_tree_mask);
        params.verified_leaf_hashes = std::move(saved.verified_leaf_hashes);
        for (auto const& tracker : extras.at("trackers"))
        {
            params.trackers.push_back(tracker.at("url").get<std::string>());
            params.tracker_tiers.push_back(tracker.at("tier").get<int>());
        }
        params.url_seeds = extras.at("web_seeds").get<std::vector<std::string>>();
        params.dht_nodes = extras.at("nodes").get<std::vector<std::pair<std::string, int>>>();
        params.comment = extras.value("comment", "");
        params.created_by = extras.value("creator", "");
        params.creation_date = extras.value("created", std::int64_t(0));
        return params;
    }
};

Catalogue::Catalogue(std::filesystem::path directory) : impl_(std::make_unique<Impl>(std::move(directory))) {}
Catalogue::~Catalogue() = default;

void Catalogue::Open(bool create)
{
    auto& state = *impl_;
    if (state.database || (!create && !std::filesystem::exists(state.path))) return;
    if (create) std::filesystem::create_directories(state.path.parent_path());
    try { state.api = std::make_unique<Sqlite>(state.condition); }
    catch (...) { state.condition = CatalogueCondition::Unavailable; throw; }
    sqlite3* database = nullptr;
    auto result = state.api->open_v2(Utf8(state.path.wstring()).c_str(), &database, 2 | (create ? 4 : 0) | 0x10000, nullptr);
    if (result != 0)
    {
        auto detail = database ? std::string(state.api->errmsg(database)) : "Cannot open catalogue";
        state.condition = (result & 0xff) == 11 || (result & 0xff) == 26 ? CatalogueCondition::Damaged : CatalogueCondition::Unavailable;
        if (database) state.api->close_v2(database);
        throw std::runtime_error(detail);
    }
    state.database = database;
    try
    {
        state.api->busy_timeout(database, 250);
        state.Execute("PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL; PRAGMA journal_mode=DELETE; "
            "PRAGMA temp_store=MEMORY; PRAGMA cache_size=-4096;");
        auto version = state.Scalar("PRAGMA user_version");
        if (version > schemaVersion) throw std::runtime_error("Catalogue was created by a newer TinyTorrent");
        if (version == 0)
        {
            Impl::Transaction transaction(state);
            state.Execute(R"SQL(
CREATE TABLE catalogue(catalogue_id INTEGER PRIMARY KEY AUTOINCREMENT,
 v1 TEXT UNIQUE CHECK(v1 IS NULL OR length(v1)=40),v2 TEXT UNIQUE CHECK(v2 IS NULL OR length(v2)=64),
 name TEXT NOT NULL,size INTEGER NOT NULL,count INTEGER NOT NULL,admitted INTEGER NOT NULL,seen INTEGER,
 selectable_count INTEGER NOT NULL DEFAULT 0,
 projected INTEGER NOT NULL DEFAULT 0,published INTEGER UNIQUE,
 CHECK(v1 IS NOT NULL OR v2 IS NOT NULL));
CREATE TABLE metadata(catalogue_id INTEGER PRIMARY KEY REFERENCES catalogue ON DELETE CASCADE,
 info BLOB NOT NULL,metainfo BLOB NOT NULL,resume BLOB NOT NULL,extras TEXT NOT NULL);
CREATE TABLE files(catalogue_id INTEGER NOT NULL REFERENCES catalogue ON DELETE CASCADE,
 file_index INTEGER NOT NULL,name TEXT NOT NULL,path TEXT NOT NULL,size INTEGER NOT NULL,
 padding INTEGER NOT NULL,symlink INTEGER NOT NULL,root TEXT CHECK(root IS NULL OR length(root)=64),extension TEXT NOT NULL,
 PRIMARY KEY(catalogue_id,file_index)) WITHOUT ROWID;
CREATE INDEX files_root ON files(root,size) WHERE root IS NOT NULL;
CREATE INDEX files_extension ON files(extension,catalogue_id,file_index);
CREATE INDEX catalogue_seen ON catalogue(seen,admitted);
CREATE INDEX catalogue_published ON catalogue(published,catalogue_id);
CREATE TABLE lookups(lookup TEXT PRIMARY KEY,catalogue_id INTEGER NOT NULL REFERENCES catalogue ON DELETE CASCADE) WITHOUT ROWID;
CREATE INDEX lookup_catalogue ON lookups(catalogue_id);
CREATE TABLE pending(lookup TEXT PRIMARY KEY,eligible INTEGER NOT NULL,failures INTEGER NOT NULL DEFAULT 0,seen INTEGER NOT NULL) WITHOUT ROWID;
CREATE INDEX pending_due ON pending(eligible);
CREATE TABLE rejections(lookup TEXT PRIMARY KEY,identity TEXT NOT NULL,decided INTEGER NOT NULL) WITHOUT ROWID;
CREATE INDEX rejections_identity ON rejections(identity);
CREATE TABLE reasons(lookup TEXT NOT NULL REFERENCES rejections ON DELETE CASCADE,fingerprint TEXT NOT NULL,reason TEXT NOT NULL,
 PRIMARY KEY(lookup,fingerprint)) WITHOUT ROWID;
CREATE TABLE nodes(address TEXT NOT NULL,port INTEGER NOT NULL,node_id TEXT NOT NULL,eligible INTEGER NOT NULL,
 PRIMARY KEY(address,port)) WITHOUT ROWID;
CREATE INDEX nodes_due ON nodes(eligible);
CREATE TABLE observations(catalogue_id INTEGER PRIMARY KEY REFERENCES catalogue ON DELETE CASCADE,
 seeds INTEGER,peers INTEGER,observed INTEGER NOT NULL,responses INTEGER NOT NULL);
CREATE TABLE state(state_id INTEGER PRIMARY KEY CHECK(state_id=1),generation INTEGER NOT NULL,publication INTEGER NOT NULL,
 rebuild_cursor INTEGER,rebuild_end INTEGER,rebuild_entry INTEGER,rebuild_active INTEGER NOT NULL DEFAULT 0);
INSERT INTO state VALUES(1,1,0,NULL,NULL,NULL,0);
PRAGMA user_version=1;
)SQL");
            transaction.Commit();
        }
        if (version < 2)
        {
            Impl::Transaction transaction(state);
            state.Execute(R"SQL(
ALTER TABLE pending ADD COLUMN peers TEXT NOT NULL DEFAULT '[]';
ALTER TABLE pending ADD COLUMN source_address TEXT NOT NULL DEFAULT '';
ALTER TABLE pending ADD COLUMN source_port INTEGER NOT NULL DEFAULT 0;
CREATE INDEX pending_peers ON pending((peers!='[]'),eligible);
CREATE TABLE totals(state_id INTEGER PRIMARY KEY CHECK(state_id=1),
 torrents INTEGER NOT NULL,files INTEGER NOT NULL,rejected INTEGER NOT NULL,
 pending INTEGER NOT NULL,ready INTEGER NOT NULL,nodes INTEGER NOT NULL,unpublished INTEGER NOT NULL);
INSERT INTO totals SELECT 1,(SELECT count(*) FROM catalogue),
 (SELECT coalesce(sum(selectable_count),0) FROM catalogue),
 (SELECT count(DISTINCT identity) FROM rejections),(SELECT count(*) FROM pending),0,
 (SELECT count(*) FROM nodes),(SELECT count(*) FROM catalogue WHERE published IS NULL);
CREATE TRIGGER total_catalogue_insert AFTER INSERT ON catalogue BEGIN
 UPDATE totals SET torrents=torrents+1,files=files+new.selectable_count,unpublished=unpublished+(new.published IS NULL); END;
CREATE TRIGGER total_catalogue_delete AFTER DELETE ON catalogue BEGIN
 UPDATE totals SET torrents=torrents-1,files=files-old.selectable_count,unpublished=unpublished-(old.published IS NULL); END;
CREATE TRIGGER total_catalogue_update AFTER UPDATE OF selectable_count,published ON catalogue BEGIN
 UPDATE totals SET files=files+new.selectable_count-old.selectable_count,
 unpublished=unpublished+(new.published IS NULL)-(old.published IS NULL); END;
CREATE TRIGGER total_pending_insert AFTER INSERT ON pending BEGIN
 UPDATE totals SET pending=pending+1,ready=ready+(new.peers!='[]'); END;
CREATE TRIGGER total_pending_delete AFTER DELETE ON pending BEGIN
 UPDATE totals SET pending=pending-1,ready=ready-(old.peers!='[]'); END;
CREATE TRIGGER total_pending_update AFTER UPDATE OF peers ON pending BEGIN
 UPDATE totals SET ready=ready+(new.peers!='[]')-(old.peers!='[]'); END;
CREATE TRIGGER total_nodes_insert AFTER INSERT ON nodes BEGIN UPDATE totals SET nodes=nodes+1; END;
CREATE TRIGGER total_nodes_delete AFTER DELETE ON nodes BEGIN UPDATE totals SET nodes=nodes-1; END;
CREATE TRIGGER total_rejection_insert AFTER INSERT ON rejections BEGIN
 UPDATE totals SET rejected=rejected+(SELECT count(*)=1 FROM rejections WHERE identity=new.identity); END;
CREATE TRIGGER total_rejection_delete AFTER DELETE ON rejections BEGIN
 UPDATE totals SET rejected=rejected-(NOT EXISTS(SELECT 1 FROM rejections WHERE identity=old.identity)); END;
CREATE TRIGGER total_rejection_update AFTER UPDATE OF identity ON rejections WHEN old.identity!=new.identity BEGIN
 UPDATE totals SET rejected=rejected-(NOT EXISTS(SELECT 1 FROM rejections WHERE identity=old.identity))
 +(SELECT count(*)=1 FROM rejections WHERE identity=new.identity); END;
PRAGMA user_version=2;
)SQL");
            transaction.Commit();
        }
        if (version < 3)
        {
            Impl::Transaction transaction(state);
            state.Execute(R"SQL(
ALTER TABLE catalogue ADD COLUMN name_key TEXT NOT NULL DEFAULT '';
ALTER TABLE state ADD COLUMN rebuilt INTEGER NOT NULL DEFAULT 0;
ALTER TABLE catalogue ADD COLUMN seeds INTEGER;
ALTER TABLE catalogue ADD COLUMN peers INTEGER;
ALTER TABLE catalogue ADD COLUMN observed INTEGER NOT NULL DEFAULT 0;
ALTER TABLE catalogue ADD COLUMN responses INTEGER NOT NULL DEFAULT 0;
UPDATE catalogue SET seeds=(SELECT seeds FROM observations o WHERE o.catalogue_id=catalogue.catalogue_id),
 peers=(SELECT peers FROM observations o WHERE o.catalogue_id=catalogue.catalogue_id),
 observed=coalesce((SELECT observed FROM observations o WHERE o.catalogue_id=catalogue.catalogue_id),0),
 responses=coalesce((SELECT responses FROM observations o WHERE o.catalogue_id=catalogue.catalogue_id),0);
DROP TABLE observations;
ALTER TABLE files RENAME TO old_files;
DROP INDEX files_root;
DROP INDEX files_extension;
CREATE TABLE files(file_id INTEGER PRIMARY KEY AUTOINCREMENT,
 catalogue_id INTEGER NOT NULL REFERENCES catalogue ON DELETE CASCADE,file_index INTEGER NOT NULL,
 name TEXT NOT NULL,path TEXT NOT NULL,size INTEGER NOT NULL,padding INTEGER NOT NULL,symlink INTEGER NOT NULL,
 root TEXT CHECK(root IS NULL OR length(root)=64),extension TEXT NOT NULL,name_key TEXT NOT NULL DEFAULT '',
 UNIQUE(catalogue_id,file_index));
INSERT INTO files(catalogue_id,file_index,name,path,size,padding,symlink,root,extension)
 SELECT catalogue_id,file_index,name,path,size,padding,symlink,root,extension FROM old_files;
DROP TABLE old_files;
CREATE INDEX files_group_name ON files(root,size,name_key,catalogue_id,file_index) WHERE root IS NOT NULL;
CREATE INDEX files_name ON files(name_key,catalogue_id,file_index);
CREATE INDEX files_size ON files(size,catalogue_id,file_index);
CREATE INDEX catalogue_name ON catalogue(name_key,catalogue_id);
CREATE INDEX catalogue_size ON catalogue(size,catalogue_id);
CREATE INDEX catalogue_seeds ON catalogue(coalesce(seeds,-1),catalogue_id);
CREATE INDEX catalogue_peers ON catalogue(coalesce(peers,-1),catalogue_id);
CREATE INDEX catalogue_last_seen ON catalogue(coalesce(seen,0),catalogue_id);
CREATE VIRTUAL TABLE file_search USING fts5(path,tokenize='trigram case_sensitive 1');
CREATE TRIGGER file_search_delete AFTER DELETE ON files BEGIN DELETE FROM file_search WHERE rowid=old.file_id; END;
CREATE VIRTUAL TABLE torrent_search USING fts5(name,tokenize='trigram case_sensitive 1');
CREATE TRIGGER torrent_search_insert AFTER INSERT ON catalogue BEGIN
 INSERT INTO torrent_search(rowid,name) VALUES(new.catalogue_id,new.name_key); END;
CREATE TRIGGER torrent_search_delete AFTER DELETE ON catalogue BEGIN DELETE FROM torrent_search WHERE rowid=old.catalogue_id; END;
)SQL");
            auto catalogueRows = state.Prepare("SELECT catalogue_id,name FROM catalogue");
            auto catalogueName = state.Prepare("UPDATE catalogue SET name_key=? WHERE catalogue_id=?");
            while (catalogueRows.Step())
            {
                catalogueName.Text(1, Utf8(Fold(catalogueRows.Text(1)))); catalogueName.Number(2, catalogueRows.Number(0));
                catalogueName.Step(); catalogueName.Reset();
            }
            state.Execute("INSERT INTO torrent_search(rowid,name) SELECT catalogue_id,name_key FROM catalogue");
            auto fileRows = state.Prepare("SELECT file_id,name,path FROM files");
            auto fileName = state.Prepare("UPDATE files SET name_key=? WHERE file_id=?");
            auto fileText = state.Prepare("INSERT INTO file_search(rowid,path) VALUES(?,?)");
            while (fileRows.Step())
            {
                fileName.Text(1, Utf8(Fold(fileRows.Text(1)))); fileName.Number(2, fileRows.Number(0));
                fileName.Step(); fileName.Reset();
                fileText.Number(1, fileRows.Number(0)); fileText.Text(2, Utf8(Fold(fileRows.Text(2))));
                fileText.Step(); fileText.Reset();
            }
            state.Execute("PRAGMA user_version=3");
            transaction.Commit();
        }
        if (version < 4)
        {
            Impl::Transaction transaction(state);
            state.Execute("CREATE INDEX pending_probe ON pending((peers!='[]'),source_address,eligible,lookup,failures,source_port)");
            state.Execute("PRAGMA user_version=4");
            transaction.Commit();
        }
        state.interruptible = database;
        state.condition = CatalogueCondition::Healthy;
    }
    catch (...)
    {
        state.database = nullptr;
        state.api->close_v2(database);
        if (state.condition == CatalogueCondition::Healthy) state.condition = CatalogueCondition::Unavailable;
        throw;
    }
}

Catalogue::Status Catalogue::ReadStatus()
{
    auto& state = *impl_;
    Status result;
    if (!state.database) return result;
    result.exists = true;
    auto totals = state.Prepare("SELECT torrents,files,rejected,pending,ready,nodes,unpublished FROM totals WHERE state_id=1");
    totals.Step();
    result.torrents = totals.Number(0);
    result.files = totals.Number(1);
    result.rejected = totals.Number(2);
    result.pending = totals.Number(3);
    result.ready = totals.Number(4);
    result.nodes = totals.Number(5);
    std::tie(result.allocated, result.reusable) = state.Footprint();
    result.projecting = totals.Number(6) != 0;
    auto rebuild = state.Prepare("SELECT generation,rebuild_active,rebuilt FROM state WHERE state_id=1");
    if (rebuild.Step())
    {
        result.generation = rebuild.Number(0);
        result.rebuilding = rebuild.Number(1) != 0;
        result.rebuilt = rebuild.Number(2);
    }
    return result;
}

CatalogueCondition Catalogue::ReadCondition() const noexcept { return impl_->condition.load(); }

Catalogue::PolicyPreview Catalogue::PreviewPolicy(Settings const& settings)
{
    PolicyPreview result;
    auto& state = *impl_;
    if (!state.database) return result;
    std::set<std::string> active;
    for (auto const& rule : settings.rules) if (rule.enabled) active.insert(Fingerprint(rule));
    auto reasons = state.Prepare("SELECT j.identity,r.fingerprint FROM reasons r JOIN rejections j USING(lookup) GROUP BY j.identity,r.fingerprint ORDER BY j.identity");
    std::string previous;
    bool retained = false;
    auto finish = [&]
    {
        if (previous.empty()) return;
        ++result.suppressed;
        if (retained) ++result.retained; else ++result.released;
    };
    while (reasons.Step())
    {
        auto key = reasons.Text(0);
        if (key != previous) { finish(); previous = key; retained = false; }
        retained |= active.contains(reasons.Text(1));
    }
    finish();
    return result;
}

void Catalogue::Configure(Settings settings)
{
    if (settings.budget <= 0 || settings.staleHours < 1) throw std::invalid_argument("Invalid catalogue storage policy");
    std::map<std::string, std::string> active;
    std::set<std::string> ids;
    for (auto const& rule : settings.rules)
    {
        if (rule.ruleId.empty() || !ids.insert(rule.ruleId).second) throw std::invalid_argument("Invalid exclusion rule identity");
        if (rule.extension.value.find_first_of("\\/*?") != std::string::npos ||
            (!rule.extension.value.empty() && rule.extension.value.find_first_not_of(".,; \t\r\n") == std::string::npos))
            throw std::invalid_argument("Invalid exclusion extensions");
        for (auto const* range : {&rule.size, &rule.count, &rule.fileSize})
            if ((range->minimum && *range->minimum < 0) || (range->maximum && *range->maximum < 0) ||
                (range->minimum && range->maximum && range->minimum > range->maximum))
                throw std::invalid_argument("Invalid exclusion range");
        if (rule.title.value.empty() && !HasRange(rule.size) && !HasRange(rule.count) &&
            rule.filename.value.empty() && rule.path.value.empty() && rule.extension.value.empty() && !HasRange(rule.fileSize))
            throw std::invalid_argument("Empty exclusion rule");
        if (rule.enabled) active.emplace(Fingerprint(rule), rule.ruleId);
    }
    auto& state = *impl_;
    if (state.database)
    {
        Impl::Transaction transaction(state);
        auto query = state.Prepare("SELECT DISTINCT fingerprint FROM reasons");
        std::vector<std::string> obsolete;
        while (query.Step()) if (!active.contains(query.Text(0))) obsolete.push_back(query.Text(0));
        auto remove = state.Prepare("DELETE FROM reasons WHERE fingerprint=?");
        for (auto const& fingerprint : obsolete)
        {
            remove.Text(1, fingerprint); remove.Step(); remove.Reset();
        }
        state.Execute("DELETE FROM rejections WHERE NOT EXISTS(SELECT 1 FROM reasons r WHERE r.lookup=rejections.lookup)");
        transaction.Commit();
    }
    if (active != state.active) state.review.reset();
    state.settings = std::move(settings);
    state.active = std::move(active);
}

Catalogue::Admission Catalogue::Admit(libtorrent::add_torrent_params const& params,
    std::string const& lookup, std::vector<char> const& metainfo, bool discovered)
{
    auto const& metadata = params.ti;
    if (!metadata || metadata->info_section().empty()) throw std::invalid_argument("Missing torrent metadata");
    Open(true);
    auto& state = *impl_;
    auto hashes = metadata->info_hashes();
    auto v1 = hashes.has_v1() ? libtorrent::aux::to_hex({hashes.v1.data(), 20}) : "";
    auto v2 = hashes.has_v2() ? libtorrent::aux::to_hex({hashes.v2.data(), 32}) : "";
    if (!lookup.empty() && lookup != v1 && lookup != v2 && lookup != v2.substr(0, 40))
        throw std::invalid_argument("Lookup does not match verified torrent metadata");
    auto files = Files(*metadata);
    libtorrent::add_torrent_params original;
    if (!metainfo.empty())
    {
        libtorrent::load_torrent_limits limits;
        limits.max_buffer_size = static_cast<int>(metainfo.size());
        limits.max_decode_depth = 200;
        original = libtorrent::load_torrent_buffer(libtorrent::span<char const>(metainfo), limits);
        if (original.ti->info_hashes() != hashes) throw std::invalid_argument("Original metainfo does not match its torrent");
    }
    std::vector<std::string> keys;
    for (auto const& key : {v1, v2, v2.substr(0, 40), lookup})
        if (!key.empty() && std::find(keys.begin(), keys.end(), key) == keys.end()) keys.push_back(key);
    std::set<std::int64_t> identities;
    for (auto const& key : keys) if (auto id = state.Find(key)) identities.insert(id);
    auto reasons = identities.empty() ? state.Reasons(metadata->name(), metadata->total_size(),
        static_cast<int>(files.size()), files) : std::vector<std::pair<std::string, std::string>>{};
    Admission result{reasons.empty(), v2.empty() ? v1 : v2, {}};
    for (auto const& [fingerprint, reason] : reasons) result.reasons.push_back(reason);
    Impl::Transaction transaction(state);
    if (!reasons.empty())
    {
        state.Remember(keys, reasons);
        transaction.Commit();
        return result;
    }
    std::int64_t id = identities.empty() ? 0 : *identities.begin();
    std::optional<std::int64_t> discoveredAt;
    if (discovered)
    {
        auto pending = state.Prepare("SELECT seen FROM pending WHERE lookup=?");
        pending.Text(1, lookup);
        if (pending.Step()) discoveredAt = pending.Number(0);
        else discoveredAt = Now();
    }
    if (identities.size() > 1)
    {
        auto aliases = state.Prepare("UPDATE lookups SET catalogue_id=? WHERE catalogue_id=?");
        auto remove = state.Prepare("DELETE FROM catalogue WHERE catalogue_id=?");
        for (auto other : identities)
        {
            if (other == id) continue;
            aliases.Number(1, id); aliases.Number(2, other); aliases.Step(); aliases.Reset();
            remove.Number(1, other); remove.Step(); remove.Reset();
        }
        state.Changed();
    }
    if (id == 0)
    {
        auto insert = state.Prepare("INSERT INTO catalogue(v1,v2,name,size,count,admitted,seen,name_key) VALUES(?,?,?,?,?,?,?,?)");
        if (v1.empty()) insert.Null(1); else insert.Text(1, v1);
        if (v2.empty()) insert.Null(2); else insert.Text(2, v2);
        insert.Text(3, metadata->name()); insert.Number(4, metadata->total_size());
        insert.Number(5, static_cast<int>(files.size())); insert.Number(6, Now());
        insert.Optional(7, discoveredAt); insert.Text(8, Utf8(Fold(metadata->name())));
        insert.Step(); id = state.api->last_insert_rowid(state.database);
    }
    else
    {
        auto update = state.Prepare("UPDATE catalogue SET v1=coalesce(v1,?),v2=coalesce(v2,?),seen=CASE WHEN ? THEN ? ELSE seen END WHERE catalogue_id=?");
        if (v1.empty()) update.Null(1); else update.Text(1, v1);
        if (v2.empty()) update.Null(2); else update.Text(2, v2);
        update.Number(3, discovered); update.Optional(4, discoveredAt); update.Number(5, id); update.Step();
    }
    Json extras = {{"trackers", Json::array()}, {"web_seeds", Json::array()}, {"nodes", Json::array()},
        {"private", metadata->priv()}, {"creator", ""}, {"comment", ""}, {"created", 0}};
    auto append = [&](char const* collection, Json const& value)
    {
        auto& values = extras[collection];
        if (std::find(values.begin(), values.end(), value) == values.end()) values.push_back(value);
    };
    libtorrent::add_torrent_params const* sources[]{&params, &original};
    for (auto const* source : sources)
    {
        for (std::size_t index = 0; index < source->trackers.size(); ++index)
            append("trackers", {{"url", source->trackers[index]},
                {"tier", index < source->tracker_tiers.size() ? source->tracker_tiers[index] : 0}});
        for (auto const& seed : source->url_seeds) append("web_seeds", seed);
        for (auto const& node : source->dht_nodes) append("nodes", node);
        if (extras["creator"].get<std::string>().empty()) extras["creator"] = source->created_by;
        if (extras["comment"].get<std::string>().empty()) extras["comment"] = source->comment;
        if (extras["created"] == 0) extras["created"] = source->creation_date;
    }
    // Resume serialization preserves partial v2 hash trees, whereas torrent-file
    // serialization requires complete piece layers. This snapshot has no transfer state.
    libtorrent::add_torrent_params snapshot;
    snapshot.ti = metadata;
    snapshot.info_hashes = hashes;
    auto originalComplete = CompleteLayers(original);
    auto const& trees = originalComplete || params.merkle_trees.empty() ? original : params;
    auto complete = originalComplete || CompleteLayers(params);
    snapshot.merkle_trees = trees.merkle_trees;
    snapshot.merkle_tree_mask = trees.merkle_tree_mask;
    snapshot.verified_leaf_hashes = trees.verified_leaf_hashes;
    auto previous = state.Prepare("SELECT extras FROM metadata WHERE catalogue_id=?");
    previous.Number(1, id);
    if (previous.Step())
    {
        auto saved = Json::parse(previous.Text(0));
        for (auto const* collection : {"trackers", "web_seeds", "nodes"})
            for (auto const& value : saved.at(collection)) append(collection, value);
        for (auto const* field : {"creator", "comment"})
            if (extras[field].get<std::string>().empty()) extras[field] = saved.value(field, "");
        if (extras["created"] == 0) extras["created"] = saved.value("created", std::int64_t(0));
        if (!complete)
        {
            auto retained = state.Load(id);
            if (snapshot.merkle_trees.empty() || CompleteLayers(retained))
            {
                snapshot.merkle_trees = std::move(retained.merkle_trees);
                snapshot.merkle_tree_mask = std::move(retained.merkle_tree_mask);
                snapshot.verified_leaf_hashes = std::move(retained.verified_leaf_hashes);
            }
        }
    }
    auto resume = libtorrent::write_resume_data_buf(snapshot);
    auto archive = state.Prepare("INSERT INTO metadata(catalogue_id,info,metainfo,resume,extras) VALUES(?,?,?,?,?) "
        "ON CONFLICT(catalogue_id) DO UPDATE SET extras=excluded.extras,resume=excluded.resume,metainfo=CASE WHEN length(excluded.metainfo)>length(metadata.metainfo) THEN excluded.metainfo ELSE metadata.metainfo END");
    auto info = metadata->info_section();
    archive.Number(1, id); archive.Blob(2, info.data(), info.size());
    archive.Blob(3, metainfo.data(), metainfo.size()); archive.Blob(4, resume.data(), resume.size());
    archive.Text(5, extras.dump()); archive.Step();
    auto selectable = state.Prepare("UPDATE catalogue SET selectable_count=? WHERE catalogue_id=?");
    selectable.Number(1, std::count_if(files.begin(), files.end(), [](File const& file) { return !file.padding; }));
    selectable.Number(2, id); selectable.Step();
    for (auto const& key : keys) state.Alias(key, id);
    transaction.Commit();
    if (!state.projection) state.projection = Impl::Projection{id, std::move(files)};
    return result;
}

void Catalogue::Discover(std::vector<std::string> const& lookups, std::int64_t now,
    std::optional<Endpoint> const& source)
{
    std::vector<Pending> candidates;
    candidates.reserve(lookups.size());
    for (auto const& lookup : lookups)
        candidates.push_back({lookup, 0, 0, {}, source});
    Discover(candidates, now);
}

void Catalogue::Discover(std::vector<Pending> const& candidates, std::int64_t now)
{
    Open(true);
    auto& state = *impl_;
    Impl::Transaction transaction(state);
    auto update = state.Prepare("UPDATE catalogue SET seen=? WHERE catalogue_id=(SELECT catalogue_id FROM lookups WHERE lookup=?)");
    auto pending = state.Prepare("INSERT INTO pending(lookup,eligible,seen,source_address,source_port) "
        "SELECT ?,?,?,?,? WHERE NOT EXISTS(SELECT 1 FROM lookups WHERE lookup=?) AND NOT EXISTS(SELECT 1 FROM rejections WHERE lookup=?) "
        "ON CONFLICT(lookup) DO UPDATE SET seen=excluded.seen,"
        "source_address=CASE WHEN excluded.source_address!='' THEN excluded.source_address ELSE pending.source_address END,"
        "source_port=CASE WHEN excluded.source_port!=0 THEN excluded.source_port ELSE pending.source_port END");
    for (auto const& candidate : candidates)
    {
        auto const& key = candidate.lookup;
        auto const& source = candidate.source;
        if ((key.size() != 40 && key.size() != 64) || key.find_first_not_of("0123456789abcdef") != std::string::npos) continue;
        update.Number(1, now); update.Text(2, key); update.Step(); update.Reset();
        pending.Text(1, key); pending.Number(2, now); pending.Number(3, now);
        pending.Text(4, source ? source->address : ""); pending.Number(5, source ? source->port : 0);
        pending.Text(6, key); pending.Text(7, key); pending.Step(); pending.Reset();
    }
    transaction.Commit();
}

Catalogue::Selection Catalogue::Triage(std::vector<Pending> const& candidates, std::int64_t now,
    std::int64_t rescrapeSeconds)
{
    Selection result;
    auto& state = *impl_;
    if (!state.database) return result;
    auto query = state.Prepare("SELECT 1 WHERE NOT EXISTS(SELECT 1 FROM lookups WHERE lookup=?) "
        "AND NOT EXISTS(SELECT 1 FROM rejections WHERE lookup=?)");
    auto known = state.Prepare("SELECT c.seeds,c.peers,c.observed FROM lookups l "
        "JOIN catalogue c USING(catalogue_id) JOIN metadata m USING(catalogue_id) "
        "WHERE l.lookup=? AND coalesce(json_extract(m.extras,'$.private'),1)=0");
    for (auto const& candidate : candidates)
    {
        query.Text(1, candidate.lookup);
        query.Text(2, candidate.lookup);
        if (query.Step()) result.missing.push_back(candidate);
        else
        {
            known.Text(1, candidate.lookup);
            if (known.Step() && (!known.Optional(0) || !known.Optional(1) ||
                known.Number(2) < now - rescrapeSeconds))
                result.scrape.push_back(candidate);
            known.Reset();
        }
        query.Reset();
    }
    return result;
}

std::vector<Catalogue::Pending> Catalogue::Due(int limit, std::int64_t now)
{
    std::vector<Pending> result;
    auto& state = *impl_;
    if (!state.database) return result;
    Impl::Transaction transaction(state);
    auto query = state.Prepare("SELECT lookup,eligible,failures,peers FROM pending WHERE (peers!='[]')=1 AND eligible<=? ORDER BY eligible,lookup LIMIT ?");
    query.Number(1, now); query.Number(2, std::clamp(limit, 0, 256));
    while (query.Step())
    {
        Pending pending{query.Text(0), query.Number(1), static_cast<int>(query.Number(2))};
        for (auto const& peer : Json::parse(query.Text(3)))
            pending.peers.push_back({peer.at(0).get<std::string>(), peer.at(1).get<int>()});
        result.push_back(std::move(pending));
    }
    auto lease = state.Prepare("UPDATE pending SET eligible=?,peers='[]' WHERE lookup=?");
    for (auto const& pending : result)
    {
        lease.Number(1, now + 120); lease.Text(2, pending.lookup); lease.Step(); lease.Reset();
    }
    transaction.Commit();
    return result;
}

std::vector<Catalogue::Pending> Catalogue::Probe(int limit, std::int64_t now)
{
    std::vector<Pending> result;
    auto& state = *impl_;
    limit = std::clamp(limit, 0, 200);
    if (!state.database || limit == 0) return result;
    Impl::Transaction transaction(state);
    std::vector<Pending> unknown;
    auto sourceless = state.Prepare("SELECT lookup,eligible,failures FROM pending WHERE (peers!='[]')=0 AND source_address='' AND eligible<=? ORDER BY eligible,lookup LIMIT ?");
    sourceless.Number(1, now); sourceless.Number(2, limit);
    while (sourceless.Step())
        unknown.push_back({sourceless.Text(0), sourceless.Number(1), static_cast<int>(sourceless.Number(2))});
    auto share = (limit + (state.preferSource ? 0 : 1)) / 2;
    auto reserved = std::min(static_cast<std::size_t>(share), unknown.size());
    result.insert(result.end(), unknown.begin(), unknown.begin() + reserved);

    auto cursor = state.probeSource;
    auto beginning = cursor;
    bool wrapped = false;
    auto next = state.Prepare("SELECT source_address FROM pending WHERE (peers!='[]')=0 AND source_address>? ORDER BY source_address LIMIT 1");
    auto query = state.Prepare("SELECT lookup,eligible,failures,source_port FROM pending WHERE (peers!='[]')=0 AND source_address=? AND eligible<=? ORDER BY eligible,lookup LIMIT 1");
    // Seek past each supplier's entire hash range, even when none is due.
    for (int visited = 0; visited < 200 && result.size() < static_cast<std::size_t>(limit);)
    {
        next.Text(1, cursor);
        if (!next.Step())
        {
            next.Reset();
            if (wrapped || beginning.empty()) break;
            cursor.clear();
            wrapped = true;
            continue;
        }
        auto address = next.Text(0);
        next.Reset();
        if (wrapped && address > beginning) break;
        cursor = std::move(address);
        ++visited;
        query.Text(1, cursor); query.Number(2, now);
        if (!query.Step()) { query.Reset(); continue; }
        Pending pending{query.Text(0), query.Number(1), static_cast<int>(query.Number(2))};
        if (query.Number(3) > 0)
            pending.source = Endpoint{cursor, static_cast<int>(query.Number(3))};
        result.push_back(std::move(pending));
        query.Reset();
    }
    auto remaining = std::min(unknown.size() - reserved, static_cast<std::size_t>(limit) - result.size());
    result.insert(result.end(), unknown.begin() + reserved, unknown.begin() + reserved + remaining);
    auto lease = state.Prepare("UPDATE pending SET eligible=? WHERE lookup=?");
    for (auto const& pending : result)
    {
        lease.Number(1, now + 120); lease.Text(2, pending.lookup); lease.Step(); lease.Reset();
    }
    transaction.Commit();
    state.probeSource = std::move(cursor);
    state.preferSource = !state.preferSource;
    return result;
}

void Catalogue::Peers(std::map<std::string, std::vector<Endpoint>> const& peers, std::int64_t now)
{
    auto& state = *impl_;
    if (!state.database || peers.empty()) return;
    Impl::Transaction transaction(state);
    auto query = state.Prepare("UPDATE pending SET peers=?,eligible=? WHERE lookup=?");
    for (auto const& [lookup, endpoints] : peers)
    {
        if (endpoints.empty()) continue;
        auto values = Json::array();
        for (auto const& peer : endpoints) values.push_back({peer.address, peer.port});
        query.Text(1, values.dump()); query.Number(2, now); query.Text(3, lookup); query.Step(); query.Reset();
    }
    transaction.Commit();
}

void Catalogue::Failed(std::set<std::string> const& lookups, std::int64_t now)
{
    auto& state = *impl_;
    if (!state.database || lookups.empty()) return;
    Impl::Transaction transaction(state);
    auto query = state.Prepare("UPDATE pending SET failures=min(failures+1,16),peers='[]',eligible=?+min(86400,60*(1<<min(failures,10))) WHERE lookup=?");
    for (auto const& lookup : lookups)
    {
        query.Number(1, now); query.Text(2, lookup); query.Step(); query.Reset();
    }
    transaction.Commit();
}

void Catalogue::SaveNodes(std::vector<Node> const& nodes)
{
    auto& state = *impl_;
    if (!state.database || nodes.empty()) return;
    Impl::Transaction transaction(state);
    auto query = state.Prepare("INSERT INTO nodes(address,port,node_id,eligible) VALUES(?,?,?,?) "
        "ON CONFLICT(address,port) DO UPDATE SET node_id=excluded.node_id,eligible=max(nodes.eligible,excluded.eligible)");
    for (auto const& node : nodes)
    {
        query.Text(1, node.address); query.Number(2, node.port); query.Text(3, node.nodeId); query.Number(4, node.eligible);
        query.Step(); query.Reset();
    }
    transaction.Commit();
}

std::vector<Catalogue::Node> Catalogue::Nodes(int limit, std::int64_t now)
{
    std::vector<Node> result;
    auto& state = *impl_;
    if (!state.database) return result;
    auto query = state.Prepare("SELECT address,port,node_id,eligible FROM nodes WHERE eligible<=? ORDER BY eligible LIMIT ?");
    query.Number(1, now); query.Number(2, std::clamp(limit, 0, 128));
    while (query.Step()) result.push_back({query.Text(0), static_cast<int>(query.Number(1)), query.Text(2), query.Number(3)});
    return result;
}

std::vector<Catalogue::Node> Catalogue::DueNodes(int limit, std::int64_t now)
{
    auto& state = *impl_;
    if (!state.database) return {};
    Impl::Transaction transaction(state);
    auto result = Nodes(limit, now);
    auto lease = state.Prepare("UPDATE nodes SET eligible=? WHERE address=? AND port=?");
    for (auto const& node : result)
    {
        lease.Number(1, now + 300); lease.Text(2, node.address); lease.Number(3, node.port); lease.Step(); lease.Reset();
    }
    transaction.Commit();
    return result;
}

Catalogue::Entry Catalogue::Detail(std::string const& hash, int offset, int limit, std::string const& query)
{
    auto& state = *impl_;
    auto id = state.Find(hash);
    if (!id) throw std::runtime_error("Catalogue entry is no longer available");
    Entry result;
    auto source = state.Prepare("SELECT " + Impl::SourceColumns() + " FROM catalogue c WHERE c.catalogue_id=?");
    source.Number(1, id); source.Step(); result.torrent = state.ReadSource(source);
    result.offset = std::max(0, offset);
    result.next = result.offset;
    auto selectable = state.Prepare("SELECT c.selectable_count,m.extras FROM catalogue c JOIN metadata m USING(catalogue_id) WHERE c.catalogue_id=?");
    selectable.Number(1, id); selectable.Step(); result.selectableCount = static_cast<int>(selectable.Number(0));
    auto extras = Json::parse(selectable.Text(1));
    result.isPrivate = extras.value("private", true);
    result.comment = extras.value("comment", "");
    result.creator = extras.value("creator", "");
    result.created = extras.value("created", std::int64_t(0));
    auto files = state.Prepare("SELECT file_index,name,path,size,padding,symlink,root FROM files WHERE catalogue_id=? AND file_index>=? ORDER BY file_index LIMIT ?");
    auto maximum = std::clamp(limit, 1, 500);
    files.Number(1, id); files.Number(2, result.offset); files.Number(3, visitLimit + 1);
    auto pattern = Fold(query);
    int visited = 0;
    auto started = std::chrono::steady_clock::now();
    ScanBudget budget(*state.api, state.database);
    try
    {
        while (files.Step())
        {
            if (++visited > visitLimit || std::chrono::steady_clock::now() - started >= scanTime)
            {
                result.more = true;
                break;
            }
            auto file = state.ReadFile(files, 0);
            if (!pattern.empty() && !Matches(Fold(file.path), pattern))
            {
                result.next = file.index + 1;
                continue;
            }
            if (result.files.size() == maximum) { result.more = true; break; }
            result.next = file.index + 1;
            result.files.push_back(std::move(file));
        }
    }
    catch (Interrupted const&) { result.more = true; }
    return result;
}

Catalogue::SourcePage Catalogue::Sources(Source const& selected, Query const& query, int offset, int limit)
{
    auto& state = *impl_;
    auto generation = query.cursor ? query.cursor->generation : 0;
    auto watermark = query.cursor ? query.cursor->watermark : 0;
    if (generation && generation != state.Generation()) throw std::runtime_error("Search results changed; refresh the query");
    auto id = state.Find(selected.hash);
    if (!id) throw std::runtime_error("Catalogue source is no longer available");
    auto file = state.Prepare("SELECT root,size FROM files WHERE catalogue_id=? AND file_index=? AND padding=0");
    file.Number(1, id); file.Number(2, selected.file.index);
    if (!file.Step()) throw std::runtime_error("Catalogue file is no longer available");
    auto root = file.Text(0);
    auto size = file.Number(1);
    auto sql = "SELECT " + Impl::SourceColumns() + ",f.file_index,f.name,f.path,f.size,f.padding,f.symlink,f.root "
        "FROM files f JOIN catalogue c USING(catalogue_id) WHERE ";
    sql += root.empty() ? "f.catalogue_id=? AND f.file_index=? " : "f.root=? AND f.size=? AND f.padding=0 ";
    sql += "AND c.published<=? ORDER BY c.catalogue_id,f.file_index LIMIT ? OFFSET ?";
    auto sources = state.Prepare(sql);
    if (root.empty()) { sources.Number(1, id); sources.Number(2, selected.file.index); }
    else { sources.Text(1, root); sources.Number(2, size); }
    if (!watermark) watermark = state.Scalar("SELECT publication FROM state WHERE state_id=1");
    sources.Number(3, watermark);
    sources.Number(4, visitLimit + 1);
    sources.Number(5, std::max(0, offset));
    Predicate predicate(query);
    SourcePage result;
    result.offset = std::max(0, offset);
    result.next = result.offset;
    int visited = 0;
    auto maximum = std::clamp(limit, 1, 200);
    auto started = std::chrono::steady_clock::now();
    ScanBudget budget(*state.api, state.database);
    try
    {
        while (sources.Step())
        {
            if (++visited > visitLimit || std::chrono::steady_clock::now() - started >= scanTime)
            {
                result.more = result.limited = true;
                break;
            }
            auto source = state.ReadSource(sources);
            source.file = state.ReadFile(sources, 11);
            if (!predicate.Parent(source)) { ++result.next; continue; }
            if (result.sources.size() == maximum) { result.more = true; break; }
            ++result.next;
            if (std::find(result.aliases.begin(), result.aliases.end(), source.file.name) == result.aliases.end())
                result.aliases.push_back(source.file.name);
            result.sources.push_back(std::move(source));
        }
    }
    catch (Interrupted const&) { result.more = result.limited = true; }
    return result;
}

libtorrent::add_torrent_params Catalogue::Metadata(std::string const& hash, Query const& scope)
{
    auto& state = *impl_;
    auto id = state.Find(hash);
    if (!id) throw std::runtime_error("Catalogue entry is no longer available");
    if (scope.cursor)
    {
        if (scope.cursor->generation != state.Generation())
            throw std::runtime_error("Search results changed; refresh the query");
        auto entry = state.Prepare("SELECT " + Impl::SourceColumns() + ",c.published "
            "FROM catalogue c WHERE c.catalogue_id=?");
        entry.Number(1, id);
        if (!entry.Step() || !entry.Optional(11) || entry.Number(11) > scope.cursor->watermark ||
            !Predicate(scope).Parent(state.ReadSource(entry)))
            throw std::runtime_error("Catalogue source is outside the captured search");
    }
    return state.Load(id);
}

void Catalogue::Observe(std::string const& hash, Observation const& observation)
{
    auto& state = *impl_;
    auto id = state.Find(hash);
    if (!id || (!observation.seeds && !observation.peers)) return;
    auto query = state.Prepare("UPDATE catalogue SET seeds=?,peers=?,observed=?,responses=? WHERE catalogue_id=? AND observed<=?");
    query.Optional(1, observation.seeds); query.Optional(2, observation.peers); query.Number(3, observation.observed);
    query.Number(4, observation.responses); query.Number(5, id); query.Number(6, observation.observed); query.Step();
    if (state.api->changes(state.database)) state.searchGroups.clear();
}

Catalogue::Page Catalogue::Search(Query const& query)
{
    auto& state = *impl_;
    Page result;
    if (!state.database) { result.exhaustive = true; return result; }
    Predicate predicate(query);
    Cursor cursor = query.cursor.value_or(Cursor{});
    if (cursor.generation && cursor.generation != state.Generation()) throw std::runtime_error("Search results changed; refresh the query");
    cursor.generation = state.Generation();
    if (!query.cursor) cursor.watermark = state.Scalar("SELECT publication FROM state WHERE state_id=1");
    result.watermark = cursor.watermark;
    result.generation = cursor.generation;
    auto key = Json::array({query.text, query.extension, query.torrent, static_cast<int>(query.target),
        static_cast<int>(query.sort), query.descending, cursor.watermark, cursor.generation,
        query.size.minimum ? Json(*query.size.minimum) : Json(nullptr),
        query.size.maximum ? Json(*query.size.maximum) : Json(nullptr)}).dump();
    if (state.searchKey != key)
    {
        state.searchKey = std::move(key);
        state.searchGroups.clear();
    }
    auto incomplete = state.Scalar("SELECT unpublished>0 FROM totals WHERE state_id=1") != 0;
    bool files = query.target == CatalogueTarget::Files;
    bool textOrder = query.sort == CatalogueSort::Name;
    std::string order, index;
    bool fileOrder = files && (textOrder || query.sort == CatalogueSort::Size);
    switch (query.sort)
    {
    case CatalogueSort::Name: order = files ? "f.name_key" : "c.name_key"; index = files ? "files_name" : "catalogue_name"; break;
    case CatalogueSort::Size: order = files ? "f.size" : "c.size"; index = files ? "files_size" : "catalogue_size"; break;
    case CatalogueSort::Seeds: order = "coalesce(c.seeds,-1)"; index = "catalogue_seeds"; break;
    case CatalogueSort::Peers: order = "coalesce(c.peers,-1)"; index = "catalogue_peers"; break;
    case CatalogueSort::Seen: order = "coalesce(c.seen,0)"; index = "catalogue_last_seen"; break;
    }
    auto candidate = predicate.Candidate();
    std::string candidates;
    if (!candidate.empty())
    {
        // Only complete small candidate sets drive a sort; common queries walk
        // the order index so a bounded continuation can advance.
        ScanBudget budget(*state.api, state.database);
        try
        {
            auto matches = state.Prepare(files
                ? "WITH matches AS MATERIALIZED(SELECT rowid FROM file_search WHERE file_search MATCH ? LIMIT 2049) "
                  "SELECT rowid FROM matches UNION ALL SELECT x.file_id FROM matches m CROSS JOIN files f CROSS JOIN files x "
                  "WHERE f.file_id=m.rowid AND x.root=f.root AND x.size=f.size AND f.root IS NOT NULL LIMIT 2049"
                : "SELECT rowid FROM torrent_search WHERE torrent_search MATCH ? LIMIT 2049");
            matches.Text(1, candidate);
            std::set<std::int64_t> ids;
            int visited = 0;
            while (matches.Step())
            {
                if (++visited > 2048) { ids.clear(); break; }
                ids.insert(matches.Number(0));
            }
            if (!visited) { result.exhaustive = !incomplete && !query.cursor; result.limited = incomplete; return result; }
            for (auto id : ids)
            {
                if (!candidates.empty()) candidates += ',';
                candidates += std::to_string(id);
            }
        }
        catch (Interrupted const&) { candidates.clear(); }
    }
    auto sql = "SELECT c.catalogue_id," + Impl::SourceColumns() + (files
        ? ",f.file_index,f.name,f.path,f.size,f.padding,f.symlink,f.root,CAST("
        : ",-1,'','',0,0,0,NULL,CAST(") + order + " AS TEXT),c.published FROM ";
    if (!files)
        sql += candidates.empty() ? "catalogue c INDEXED BY " + index + " WHERE 1=1 "
            : "catalogue c WHERE c.catalogue_id IN(" + candidates + ") ";
    else
        sql += !candidates.empty() ? "files f CROSS JOIN catalogue c WHERE c.catalogue_id=f.catalogue_id AND f.file_id IN(" + candidates + ") "
            : fileOrder ? "files f INDEXED BY " + index + " CROSS JOIN catalogue c WHERE c.catalogue_id=f.catalogue_id "
            : "catalogue c INDEXED BY " + index + " CROSS JOIN files f WHERE f.catalogue_id=c.catalogue_id ";
    auto direction = query.descending ? " DESC" : " ASC";
    auto comparison = query.descending ? "<" : ">";
    auto entryColumn = fileOrder ? "f.catalogue_id" : "c.catalogue_id";
    auto ordering = " ORDER BY " + order + direction + ',' + entryColumn + direction;
    if (files) ordering += ",f.file_index" + std::string(direction);
    std::vector<std::pair<std::string, int>> slices;
    if (!cursor.value.empty() && !fileOrder && candidates.empty())
    {
        // Separate equal-rank seeks keep the large unknown-availability cohort
        // and a torrent's large file list from being rescanned on every page.
        if (files)
            slices.emplace_back(sql + "AND " + order + "=?1 AND c.catalogue_id=?2 AND f.file_index" +
                comparison + "?3 ORDER BY f.file_index" + direction, 3);
        slices.emplace_back(sql + "AND " + order + "=?1 AND c.catalogue_id" + comparison +
            "?2 ORDER BY c.catalogue_id" + direction + (files ? ",f.file_index" + std::string(direction) : ""), 2);
        slices.emplace_back(sql + "AND " + order + comparison + "?1" + ordering, 1);
    }
    else
    {
        if (!cursor.value.empty())
            sql += "AND (" + order + ',' + entryColumn + (files ? ",f.file_index)" : ")") + comparison +
                (files ? "(?1,?2,?3) " : "(?1,?2) ");
        slices.emplace_back(sql + ordering, cursor.value.empty() ? 0 : files ? 3 : 2);
    }
    auto continuation = cursor;
    auto maximum = std::clamp(query.limit, 1, 200);
    auto start = std::chrono::steady_clock::now();
    bool stopped = false;
    ScanBudget budget(*state.api, state.database);
    try
    {
        for (auto const& [statement, parameters] : slices)
        {
            auto rows = state.Prepare(statement);
            if (parameters)
            {
                if (textOrder) rows.Text(1, continuation.value);
                else rows.Number(1, std::stoll(continuation.value));
                if (parameters >= 2) rows.Number(2, continuation.entry);
                if (parameters >= 3) rows.Number(3, continuation.file);
            }
            while (rows.Step())
            {
                if (++result.visited > visitLimit || std::chrono::steady_clock::now() - start >= scanTime)
                { stopped = true; break; }
                auto rowCursor = cursor;
                rowCursor.entry = rows.Number(0);
                rowCursor.file = static_cast<int>(rows.Number(12));
                rowCursor.value = rows.Text(19);
                auto source = state.ReadSource(rows, 1);
                source.file = state.ReadFile(rows, 12);
                if (!rows.Optional(20) || rows.Number(20) > cursor.watermark || source.file.padding)
                { cursor = rowCursor; continue; }
                Hit hit;
                bool matched = false;
                if (files && !source.file.root.empty())
                {
                    auto groupKey = std::pair(source.file.root, source.file.size);
                    if (!state.searchGroups.contains(groupKey) && state.searchGroups.size() >= 64)
                        state.searchGroups.erase(state.searchGroups.begin());
                    auto& group = state.searchGroups[groupKey];
                    if (!group.complete)
                    {
                        auto aliasSql = "SELECT c.catalogue_id," + Impl::SourceColumns() +
                            ",f.file_index,f.name,f.path,f.size,f.padding,f.symlink,f.root,f.name_key,c.published "
                            "FROM files f INDEXED BY files_group_name CROSS JOIN catalogue c "
                            "WHERE c.catalogue_id=f.catalogue_id AND f.root=?1 AND f.size=?2 ";
                        if (group.after)
                            aliasSql += "AND (f.name_key,f.catalogue_id,f.file_index)>(?3,?4,?5) ";
                        aliasSql += "ORDER BY f.name_key,f.catalogue_id,f.file_index";
                        auto aliases = state.Prepare(aliasSql);
                        aliases.Text(1, source.file.root); aliases.Number(2, source.file.size);
                        if (group.after)
                        {
                            aliases.Text(3, group.after->value); aliases.Number(4, group.after->entry); aliases.Number(5, group.after->file);
                        }
                        auto score = [](Source const& value)
                            { return std::tuple(value.availability.seeds.value_or(-1), value.availability.peers.value_or(-1)); };
                        while (aliases.Step())
                        {
                            if (++result.visited > visitLimit || std::chrono::steady_clock::now() - start >= scanTime)
                            { stopped = true; break; }
                            auto alias = state.ReadSource(aliases, 1);
                            alias.file = state.ReadFile(aliases, 12);
                            group.after = Cursor{aliases.Number(0), alias.file.index, cursor.watermark, cursor.generation, aliases.Text(19)};
                            if (!aliases.Optional(20) || aliases.Number(20) > cursor.watermark || alias.file.padding) continue;
                            if (!predicate.Parent(alias)) continue;
                            if (!group.matched && predicate.Matches(alias, true))
                            { group.matched = aliases.Number(0); group.hit.matched = alias; }
                            if (!group.preferred || score(alias) > score(group.hit.preferred) ||
                                (score(alias) == score(group.hit.preferred) &&
                                 std::tie(alias.hash, alias.file.index) < std::tie(group.hit.preferred.hash, group.hit.preferred.file.index)))
                            { group.preferred = aliases.Number(0); group.hit.preferred = alias; }
                            if (group.hit.sources.size() < sourceLimit)
                            {
                                group.hit.sources.push_back(alias);
                                if (std::find(group.hit.aliases.begin(), group.hit.aliases.end(), alias.file.name) == group.hit.aliases.end())
                                    group.hit.aliases.push_back(alias.file.name);
                            }
                            else group.hit.sourcesLimited = true;
                        }
                        if (stopped) break;
                        group.complete = true;
                    }
                    hit = group.hit;
                    bool availabilityOrder = query.sort == CatalogueSort::Seeds || query.sort == CatalogueSort::Peers;
                    matched = group.matched && (availabilityOrder
                        ? group.preferred == rowCursor.entry && hit.preferred.file.index == rowCursor.file
                        : group.matched == rowCursor.entry && hit.matched.file.index == rowCursor.file);
                }
                else if (predicate.Matches(source, files))
                {
                    matched = true;
                    hit.matched = hit.preferred = source;
                    hit.sources.push_back(source);
                    if (files) hit.aliases.push_back(source.file.name);
                }
                cursor = rowCursor;
                if (matched) result.hits.push_back(std::move(hit));
                if (result.hits.size() >= maximum) { stopped = true; break; }
            }
            if (stopped) break;
        }
    }
    catch (Interrupted const&) { stopped = true; }
    result.limited = stopped || incomplete;
    result.exhaustive = !stopped && !incomplete && !query.cursor;
    if (stopped) result.next = cursor;
    return result;
}

Catalogue::Removal Catalogue::Preview(CatalogueRemoval kind)
{
    auto& state = *impl_;
    if (!state.database) return {};
    auto generation = state.Generation();
    if (!state.review || state.review->kind != kind || state.review->generation != generation || state.review->complete)
    {
        auto watermark = state.Scalar("SELECT coalesce(max(catalogue_id),0) FROM catalogue");
        auto token = std::to_string(generation) + ":" + std::to_string(++state.reviewSequence) + ":" + std::to_string(watermark);
        state.review = Impl::Review{kind, watermark, generation};
        state.review->result.token = std::move(token);
    }
    auto& review = *state.review;
    auto entries = state.Prepare("SELECT catalogue_id,name,size,count FROM catalogue WHERE catalogue_id>? AND catalogue_id<=? ORDER BY catalogue_id LIMIT 17");
    entries.Number(1, review.cursor); entries.Number(2, review.watermark);
    int visited = 0;
    review.result.more = false;
    auto started = std::chrono::steady_clock::now();
    while (entries.Step())
    {
        if (++visited > 16 || std::chrono::steady_clock::now() - started >= scanTime)
        {
            review.result.more = true;
            break;
        }
        auto id = entries.Number(0);
        review.cursor = id;
        bool remove = kind == CatalogueRemoval::Clear;
        if (!remove)
        {
            auto retained = state.Load(id);
            remove = !state.Reasons(entries.Text(1), entries.Number(2), static_cast<int>(entries.Number(3)), Files(*retained.ti)).empty();
        }
        if (remove)
        {
            ++review.result.torrents;
            review.result.files += entries.Number(3);
            auto bytes = state.Prepare("SELECT length(info)+length(metainfo)+length(resume)+length(extras) FROM metadata WHERE catalogue_id=?");
            bytes.Number(1, id); if (bytes.Step()) review.result.bytes += bytes.Number(0);
        }
    }
    review.complete = !review.result.more;
    return review.result;
}

Catalogue::RemovalPage Catalogue::PreviewRows(std::string const& token, std::int64_t after, int limit)
{
    auto& state = *impl_;
    if (!state.review || state.review->result.token != token || state.review->generation != state.Generation())
        throw std::runtime_error("Removal preview changed; review it again");
    auto& review = *state.review;
    auto entries = state.Prepare("SELECT c.catalogue_id," + Impl::SourceColumns() +
        " FROM catalogue c WHERE c.catalogue_id>? AND c.catalogue_id<=? ORDER BY c.catalogue_id");
    entries.Number(1, after); entries.Number(2, review.watermark);
    RemovalPage result;
    result.next = after;
    auto maximum = std::clamp(limit, 1, 100);
    auto started = std::chrono::steady_clock::now();
    while (entries.Step())
    {
        if (std::chrono::steady_clock::now() - started >= scanTime || result.entries.size() >= maximum)
        {
            result.more = true;
            break;
        }
        auto id = entries.Number(0);
        result.next = id;
        RemovalRow item;
        item.torrent = state.ReadSource(entries, 1);
        if (review.kind == CatalogueRemoval::Excluded)
        {
            auto retained = state.Load(id);
            auto files = Files(*retained.ti);
            for (auto const& rule : state.settings.rules)
                if (Rejects(rule, item.torrent.name, item.torrent.size, item.torrent.count, files))
                    item.reasons.push_back(rule.ruleId);
            if (item.reasons.empty()) continue;
        }
        result.entries.push_back(std::move(item));
    }
    return result;
}

Catalogue::Removal Catalogue::Remove(std::string const& token, int limit)
{
    auto& state = *impl_;
    if (!state.review || state.review->result.token != token || !state.review->complete ||
        state.review->generation != state.Generation()) throw std::runtime_error("Removal preview changed; review it again");
    auto& review = *state.review;
    auto entries = state.Prepare("SELECT catalogue_id,name,size,count FROM catalogue WHERE catalogue_id>? AND catalogue_id<=? ORDER BY catalogue_id LIMIT ?");
    entries.Number(1, review.applied); entries.Number(2, review.watermark); entries.Number(3, std::clamp(limit, 1, 64));
    struct Candidate { std::int64_t id; std::vector<std::pair<std::string, std::string>> reasons; };
    std::vector<Candidate> candidates;
    int visited = 0;
    auto applied = review.applied;
    auto started = std::chrono::steady_clock::now();
    while (entries.Step())
    {
        if (std::chrono::steady_clock::now() - started >= scanTime) break;
        ++visited;
        auto id = entries.Number(0); applied = id;
        std::vector<std::pair<std::string, std::string>> reasons;
        if (review.kind == CatalogueRemoval::Excluded)
        {
            auto retained = state.Load(id);
            reasons = state.Reasons(entries.Text(1), entries.Number(2), static_cast<int>(entries.Number(3)), Files(*retained.ti));
            if (reasons.empty()) continue;
        }
        candidates.push_back({id, std::move(reasons)});
    }
    Impl::Transaction transaction(state);
    std::int64_t removed = 0;
    auto remove = state.Prepare("DELETE FROM catalogue WHERE catalogue_id=?");
    for (auto const& candidate : candidates)
    {
        if (!candidate.reasons.empty())
        {
            auto aliases = state.Prepare("SELECT lookup FROM lookups WHERE catalogue_id=?");
            aliases.Number(1, candidate.id);
            std::vector<std::string> keys;
            while (aliases.Step()) keys.push_back(aliases.Text(0));
            state.Remember(keys, candidate.reasons);
        }
        remove.Number(1, candidate.id); remove.Step(); remove.Reset(); ++removed;
    }
    if (!candidates.empty())
    {
        state.Execute("UPDATE state SET generation=generation+1 WHERE state_id=1");
        review.generation = state.Generation();
    }
    transaction.Commit();
    review.applied = applied;
    review.result.removed += removed;
    auto remaining = state.Prepare("SELECT 1 FROM catalogue WHERE catalogue_id>? AND catalogue_id<=? LIMIT 1");
    remaining.Number(1, applied); remaining.Number(2, review.watermark);
    review.result.more = remaining.Step();
    auto result = review.result;
    if (!result.more) state.review.reset();
    return result;
}

Catalogue::Progress Catalogue::Rebuild(int limit)
{
    auto& state = *impl_;
    if (!state.database) return {};
    if (state.Scalar("SELECT rebuild_cursor IS NULL FROM state WHERE state_id=1"))
    {
        auto end = state.Scalar("SELECT coalesce(max(catalogue_id),0) FROM catalogue");
        auto start = state.Prepare("UPDATE state SET rebuild_cursor=0,rebuild_end=?,rebuild_entry=NULL,rebuild_active=1,rebuilt=0,generation=generation+1 WHERE state_id=1");
        start.Number(1, end); start.Step(); state.review.reset();
    }
    else state.Execute("UPDATE state SET rebuild_active=1 WHERE state_id=1");
    return ProjectPending(limit);
}

Catalogue::Progress Catalogue::ProjectPending(int limit)
{
    auto& state = *impl_;
    if (!state.database) return {};
    auto deadline = std::chrono::steady_clock::now() + scanTime;
    int remaining = std::clamp(limit, 1, 2048);
    int published = 0;
    while (remaining > 0 && std::chrono::steady_clock::now() < deadline)
    {
        auto progress = state.Prepare("SELECT rebuild_active,rebuild_cursor,rebuild_end,rebuild_entry FROM state WHERE state_id=1");
        progress.Step();
        bool rebuilding = progress.Number(0) != 0;
        if (rebuilding && !progress.Optional(3))
        {
            auto next = state.Prepare("SELECT catalogue_id FROM catalogue WHERE catalogue_id>? AND catalogue_id<=? ORDER BY catalogue_id LIMIT 1");
            next.Number(1, progress.Number(1)); next.Number(2, progress.Number(2));
            if (!next.Step())
            {
                state.Execute("UPDATE state SET rebuild_cursor=NULL,rebuild_end=NULL,rebuild_entry=NULL,rebuild_active=0 WHERE state_id=1");
                break;
            }
            auto id = next.Number(0);
            Impl::Transaction transaction(state);
            auto remove = state.Prepare("DELETE FROM files WHERE catalogue_id=?");
            remove.Number(1, id); remove.Step();
            auto reset = state.Prepare("UPDATE catalogue SET published=NULL,projected=0 WHERE catalogue_id=?");
            reset.Number(1, id); reset.Step();
            auto current = state.Prepare("UPDATE state SET rebuild_entry=?,generation=generation+1 WHERE state_id=1");
            current.Number(1, id); current.Step(); transaction.Commit();
            state.projection.reset();
        }
        auto entry = state.Prepare(rebuilding
            ? "SELECT catalogue_id,projected FROM catalogue WHERE catalogue_id=(SELECT rebuild_entry FROM state WHERE state_id=1)"
            : "SELECT catalogue_id,projected FROM catalogue WHERE published IS NULL ORDER BY catalogue_id LIMIT 1");
        if (!entry.Step())
        {
            state.projection.reset();
            if (rebuilding)
            {
                state.Execute("UPDATE state SET rebuild_entry=NULL WHERE state_id=1");
                continue;
            }
            break;
        }
        auto id = entry.Number(0);
        auto begin = static_cast<int>(entry.Number(1));
        if (!state.projection || state.projection->entry != id)
        {
            auto retained = state.Load(id);
            state.projection = Impl::Projection{id, Files(*retained.ti)};
        }
        auto const& files = state.projection->files;
        if (begin < 0 || begin > files.size()) throw std::runtime_error("Invalid saved projection progress");
        Impl::Transaction transaction(state);
        auto next = state.Project(id, files, begin, remaining, deadline);
        remaining -= std::max(1, next - begin);
        bool complete = next == files.size();
        if (complete) state.Execute("UPDATE state SET publication=publication+1 WHERE state_id=1");
        auto update = state.Prepare("UPDATE catalogue SET projected=?,published=? WHERE catalogue_id=?");
        update.Number(1, next);
        if (complete) update.Number(2, state.Scalar("SELECT publication FROM state WHERE state_id=1")); else update.Null(2);
        update.Number(3, id); update.Step();
        if (complete)
        {
            auto saved = state.Prepare("UPDATE state SET rebuild_cursor=?,rebuild_entry=NULL,rebuilt=rebuilt+1 WHERE rebuild_entry=?");
            saved.Number(1, id); saved.Number(2, id); saved.Step();
        }
        transaction.Commit();
        // Finishing a cancelled rebuild is not a newly indexed torrent.
        if (complete && !rebuilding && progress.Optional(3) != id)
            ++published;
        if (complete) state.projection.reset();
        else break;
    }
    auto result = state.ReadProgress();
    result.published = published;
    return result;
}

void Catalogue::CancelRebuild()
{
    if (impl_->database) impl_->Execute("UPDATE state SET rebuild_active=0 WHERE state_id=1");
}

int Catalogue::Evict(std::int64_t now, int limit)
{
    auto& state = *impl_;
    if (!state.database) return 0;
    auto [allocated, reusable] = state.Footprint();
    if (allocated - reusable < state.settings.budget) return 0;
    auto entries = state.Prepare("SELECT catalogue_id FROM catalogue WHERE coalesce(seen,admitted)<? ORDER BY coalesce(seen,admitted),catalogue_id LIMIT ?");
    auto cutoff = now - static_cast<std::int64_t>(state.settings.staleHours) * 3600;
    entries.Number(1, cutoff); entries.Number(2, std::clamp(limit, 1, 64));
    std::vector<std::int64_t> ids;
    while (entries.Step()) ids.push_back(entries.Number(0));
    int removed = 0;
    for (auto id : ids)
    {
        std::tie(allocated, reusable) = state.Footprint();
        if (allocated - reusable < state.settings.budget) break;
        Impl::Transaction transaction(state);
        auto remove = state.Prepare("DELETE FROM catalogue WHERE catalogue_id=? AND coalesce(seen,admitted)<?");
        remove.Number(1, id); remove.Number(2, cutoff); remove.Step();
        auto changed = state.api->changes(state.database);
        if (changed) state.Execute("UPDATE state SET generation=generation+1 WHERE state_id=1");
        transaction.Commit();
        removed += changed;
    }
    if (removed) state.review.reset();
    return removed;
}

void Catalogue::Interrupt()
{
    auto database = impl_->interruptible.load();
    if (database && impl_->api) impl_->api->interrupt(database);
}
}
