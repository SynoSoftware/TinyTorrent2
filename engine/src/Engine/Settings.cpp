#include "Engine/State.h"
#include <Windows.h>
#include <ShlObj.h>
#include <wincrypt.h>
#include "Strings.h"
#include <algorithm>
#include <climits>
#include <cmath>
#include <expected>
#include <span>
#include <stdexcept>
#include <utility>

namespace tt
{
namespace
{
// The keys that code outside Settings::Fields names, in settings.json and in
// the settings command and reply. A key is file format: renaming one needs a
// migration.
namespace setting
{
constexpr char lastFolder[] = "last_folder";
constexpr char additionFolder[] = "addition_destination";
constexpr char incompleteFolder[] = "incomplete_folder";
constexpr char language[] = "language";
constexpr char networkInterface[] = "network_interface";
constexpr char proxyPassword[] = "proxy_password";
constexpr char schedule[] = "schedule";
constexpr char allPaused[] = "all_paused";
constexpr char limitMode[] = "limit_mode";
constexpr char reportedPrograms[] = "reported_programs";
}

// The values a setting accepts. A settings command refuses any other value;
// loading takes the default instead, or the nearest accepted value where a
// domain names one, as the saved-value recovery ruling in architecture.md
// requires.
namespace accepts
{
struct Bool
{
    bool Accepts(Json const& value) const { return value.is_boolean(); }
    bool From(Json const& value) const { return value.get<bool>(); }
};

// A whole number from `minimum` to `maximum`.
struct Range
{
    int minimum;
    int maximum;

    bool Accepts(Json const& value) const
    {
        return value.is_number_integer() && value >= minimum && value <= maximum;
    }
    int From(Json const& value) const { return value.get<int>(); }
    std::optional<int> Nearest(Json const& value) const
    {
        if (!value.is_number_integer())
        {
            return std::nullopt;
        }
        return value < minimum ? minimum : maximum;
    }
};

// A limit, where 0 means no limit and any other value runs from `minimum`
// to `maximum`.
struct Limit
{
    int minimum;
    int maximum;

    bool Accepts(Json const& value) const
    {
        return value.is_number_integer() && (value == 0 || (value >= minimum && value <= maximum));
    }
    int From(Json const& value) const { return value.get<int>(); }
    // A negative number takes the default, because the nearest value, 0,
    // would turn the person's limit into no limit. So does a number no int
    // holds, which no setting saves.
    std::optional<int> Nearest(Json const& value) const
    {
        if (!value.is_number_integer() || value < 0 || value > INT_MAX)
        {
            return std::nullopt;
        }
        return value < minimum ? minimum : maximum;
    }
};

// One of a few whole numbers.
struct Choice
{
    std::span<int const> values;

    bool Accepts(Json const& value) const
    {
        return value.is_number_integer() &&
            std::ranges::any_of(values, [&value](int choice) { return value == choice; });
    }
    int From(Json const& value) const { return value.get<int>(); }
};

// A protocol word from `names`.
template <typename Value, std::size_t Size>
struct Words
{
    std::pair<std::string_view, Value> const (&names)[Size];

    std::optional<Value> Find(Json const& value) const
    {
        return value.is_string() ? Parse(names, value.get_ref<std::string const&>()) : std::nullopt;
    }
    bool Accepts(Json const& value) const { return Find(value).has_value(); }
    Value From(Json const& value) const { return *Find(value); }
    std::string_view Write(Value value) const { return Word(names, value); }
};

template <typename Value, std::size_t Size>
Words(std::pair<std::string_view, Value> const (&)[Size]) -> Words<Value, Size>;

// Text of at most `limit` characters that `valid` accepts.
struct Text
{
    std::size_t limit = std::string::npos;
    bool (*valid)(std::string const& text) = nullptr;

    bool Accepts(Json const& value) const
    {
        if (!value.is_string())
        {
            return false;
        }
        auto const& text = value.get_ref<std::string const&>();
        return text.size() <= limit && (!valid || valid(text));
    }
    std::string From(Json const& value) const { return value.get<std::string>(); }
};

// A finite number from 0 to `maximum`.
struct Quantity
{
    double maximum = std::numeric_limits<double>::infinity();

    bool Accepts(Json const& value) const
    {
        return value.is_number() && value >= 0 && value <= maximum && std::isfinite(value.get<double>());
    }
    double From(Json const& value) const { return value.get<double>(); }
};
}

constexpr std::size_t periodLimit = 128;
// SOCKS5 sends the user name and the password each after a one-byte length.
constexpr std::size_t credentialLimit = 255;
// The longest DNS name.
constexpr std::size_t hostLimit = 253;
// The largest capacity in megabits per second whose bytes per second fit an
// int.
constexpr double capacityLimit = double(INT_MAX) * 8 / 1'000'000;

constexpr std::pair<std::string_view, Encryption> encryptions[] = {
    {"preferred", Encryption::Preferred},
    {"required", Encryption::Required},
    {"allowed", Encryption::Allowed},
    {"disabled", Encryption::Disabled}};

constexpr std::pair<std::string_view, ProxyType> proxyTypes[] = {
    {"none", ProxyType::None},
    {"socks5", ProxyType::Socks5},
    {"socks4", ProxyType::Socks4},
    {"http", ProxyType::Http}};

constexpr std::pair<std::string_view, Transport> transports[] = {
    {"both", Transport::Both},
    {"tcp", Transport::Tcp},
    {"utp", Transport::Utp}};

constexpr std::pair<std::string_view, IpFamily> ipFamilies[] = {
    {"both", IpFamily::Both},
    {"ipv4", IpFamily::Ipv4},
    {"ipv6", IpFamily::Ipv6}};

constexpr std::pair<std::string_view, Layout> layouts[] = {
    {"keep", Layout::Keep}, {"create", Layout::Create}, {"strip", Layout::Strip}};
constexpr std::pair<std::string_view, DuplicatePolicy> duplicatePolicies[] = {
    {"keep", DuplicatePolicy::Keep}, {"merge", DuplicatePolicy::Merge}};
constexpr std::pair<std::string_view, SeedRule> seedRules[] = {
    {"any", SeedRule::Any}, {"all", SeedRule::All}};
constexpr std::pair<std::string_view, Theme> themes[] = {
    {"system", Theme::System}, {"light", Theme::Light}, {"dark", Theme::Dark}};
constexpr std::pair<std::string_view, TaskAnswer> taskAnswers[] = {
    {"ask", TaskAnswer::Ask}, {"continue", TaskAnswer::Continue}, {"abandon", TaskAnswer::Abandon}};

constexpr int recentIntervals[] = {1, 5, 10};
constexpr int historyIntervals[] = {10, 30, 60, 300};

// The password is encrypted for the current Windows user, so a copy of
// settings.json does not reveal it.
std::expected<std::string, Outcome> Protect(std::string const& secret)
{
    if (secret.empty())
    {
        return std::string();
    }
    DATA_BLOB input{static_cast<DWORD>(secret.size()), reinterpret_cast<BYTE*>(const_cast<char*>(secret.data()))};
    DATA_BLOB output{};
    if (!CryptProtectData(&input, nullptr, nullptr, nullptr, nullptr, CRYPTPROTECT_UI_FORBIDDEN, &output))
    {
        return std::unexpected(Outcome{ErrorCode::EncryptionFailed, "CryptProtectData: " + std::to_string(GetLastError())});
    }
    std::string bytes(reinterpret_cast<char const*>(output.pbData), output.cbData);
    LocalFree(output.pbData);
    return Base64(bytes);
}

// Another Windows user, or another PC, cannot decrypt the password. The proxy
// then refuses the sign-in instead of the settings failing to load.
std::string Unprotect(std::string const& text)
{
    DWORD size = 0;
    if (text.empty() ||
        !CryptStringToBinaryA(text.c_str(), 0, CRYPT_STRING_BASE64, nullptr, &size, nullptr, nullptr))
    {
        return {};
    }
    std::vector<BYTE> bytes(size);
    CryptStringToBinaryA(text.c_str(), 0, CRYPT_STRING_BASE64, bytes.data(), &size, nullptr, nullptr);
    DATA_BLOB input{size, bytes.data()};
    DATA_BLOB output{};
    if (!CryptUnprotectData(&input, nullptr, nullptr, nullptr, nullptr, CRYPTPROTECT_UI_FORBIDDEN, &output))
    {
        return {};
    }
    std::string secret(reinterpret_cast<char const*>(output.pbData), output.cbData);
    SecureZeroMemory(output.pbData, output.cbData);
    LocalFree(output.pbData);
    return secret;
}

// A host name or an IP address, as far as the engine can tell without
// resolving it.
bool IsHost(std::string const& host)
{
    return !host.empty() && host.size() <= hostLimit && std::none_of(host.begin(), host.end(),
        [](unsigned char character) { return character <= 0x20 || character == 0x7F; });
}
}

char const* Engine::State::Settings::Name(LimitMode mode)
{
    switch (mode)
    {
    case LimitMode::Speed:
        return "speed";
    case LimitMode::Alternative:
        return "alternative";
    default:
        return "none";
    }
}

std::optional<LimitMode> Engine::State::Settings::Named(Json const& value)
{
    for (auto mode : {LimitMode::None, LimitMode::Speed, LimitMode::Alternative})
    {
        if (value == Name(mode))
        {
            return mode;
        }
    }
    return std::nullopt;
}

auto Engine::State::Settings::Switches() -> std::span<Switch const>
{
    static constexpr Switch switches[] = {
        {Setting::ConfirmExit, "confirm_exit", &Settings::confirmsExit, {}},
        {Setting::NotifyFinished, "notifications_enabled", &Settings::notificationsEnabled, "finished"},
        {Setting::NotifyAdded, "notify_added", &Settings::notifiesAdded, "added"},
        {Setting::NotifyProblems, "notify_problems", &Settings::notifiesProblems, "problems"},
        {Setting::NotifyBackground, "notify_background", &Settings::notifiesBackground, "background"}};
    return switches;
}

auto Engine::State::Settings::SwitchFor(Setting setting) -> Switch const&
{
    for (auto const& entry : Switches())
    {
        if (entry.setting == setting)
            return entry;
    }
    throw std::invalid_argument("Unknown opt-out setting.");
}

Engine::State::Settings::Limits Engine::State::Settings::Caps(LimitMode mode) const
{
    switch (mode)
    {
    case LimitMode::Speed:
        return limits;
    case LimitMode::Alternative:
        return alternative;
    default:
        return {};
    }
}

Engine::State::Settings Engine::State::Defaults()
{
    Settings defaults;
    PWSTR downloads = nullptr;
    if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_Downloads, 0, nullptr, &downloads)))
    {
        defaults.destination = Utf8(downloads);
        defaults.incompleteFolder = Utf8((std::filesystem::path(downloads) / L"Incomplete").wstring());
        CoTaskMemFree(downloads);
    }
    defaults.language = Strings::DefaultLanguage();
    return defaults;
}

template <typename Self, typename Visit>
void Engine::State::Settings::Fields(Self& settings, Visit&& visit)
{
    using namespace accepts;
    auto folder = [](std::string const& path) { return IsAbsolute(path); };
    auto optionalFolder = [](std::string const& path) { return path.empty() || IsAbsolute(path); };
    auto isLanguage = [](std::string const& code) { return Strings::Supports(code); };
    auto host = [](std::string const& name) { return name.empty() || IsHost(name); };
    visit("default_destination", settings.destination, Text{.valid = folder});
    visit("starts_download", settings.startsDownload, Bool{});
    visit("queue_top", settings.queueTop, Bool{});
    visit("use_last_folder", settings.usesLastFolder, Bool{});
    visit("start_paused", settings.startsPaused, Bool{});
    visit("raise_add", settings.raisesAdd, Bool{});
    visit("layout", settings.layout, Words{layouts});
    visit("duplicates", settings.duplicates, Words{duplicatePolicies});
    visit("exclude", settings.excludes, Bool{});
    visit("patterns", settings.patterns, Text{.limit = patternLimit});
    visit("inactive_time", settings.inactiveMinutes, Limit{0, 525600});
    visit("seed_rule", settings.seedRule, Words{seedRules});
    visit("watch", settings.watches, Bool{});
    visit("watch_path", settings.watchPath, Text{.valid = optionalFolder});
    visit("watch_recursive", settings.watchesRecursively, Bool{});
    visit("watch_destination", settings.watchDestination, Text{.valid = optionalFolder});
    visit("recheck_finished", settings.rechecksFinished, Bool{});
    visit(setting::incompleteFolder, settings.incompleteFolder, Text{.valid = folder});
    visit("use_incomplete_folder", settings.usesIncompleteFolder, Bool{});
    visit("append_suffix", settings.appendsSuffix, Bool{});
    for (auto const& entry : Switches())
        visit(entry.name, settings.*entry.field, Bool{});
    visit("show_external_ip", settings.showsExternalIp, Bool{});
    visit("show_title_speeds", settings.showsTitleSpeeds, Bool{});
    visit("show_free_space", settings.showsFreeSpace, Bool{});
    visit("show_memory", settings.showsMemory, Bool{});
    visit("refresh_interval", settings.refreshInterval, Range{1000, 10000});
    visit("recent_interval", settings.recentInterval, Choice{recentIntervals});
    visit("history_interval", settings.historyInterval, Choice{historyIntervals});
    visit("disk_buffer_mib", settings.diskBufferMib, Range{1, 1024});
    visit("checking_memory_mib", settings.checkingMib, Range{1, 1024});
    visit("hashing_threads", settings.hashingThreads, Range{1, 64});
    visit("file_pool_size", settings.fileLimit, Range{1, 10000});
    visit(setting::language, settings.language, Text{.valid = isLanguage});
    visit("theme", settings.theme, Words{themes});
    visit("port_mapping", settings.mapsPorts, Bool{});
    visit("listen_port", settings.listenPort, Range{1, 65535});
    visit("active_downloads", settings.activeDownloads, Limit{0, INT_MAX});
    visit("active_seeds", settings.activeSeeds, Limit{0, INT_MAX});
    visit("connection_limit", settings.connections, Limit{0, INT_MAX});
    visit("active_total", settings.activeTotal, Limit{0, INT_MAX});
    visit("capacity_download", settings.capacityDownload, Quantity{capacityLimit});
    visit("capacity_upload", settings.capacityUpload, Quantity{capacityLimit});
    visit("per_torrent_connections", settings.torrentConnections, Limit{2, 10000});
    visit("ignore_slow", settings.ignoresSlow, Bool{});
    visit("slow_download", settings.slowDownload, Range{0, 1024 * 1024 * 1024});
    visit("slow_upload", settings.slowUpload, Range{0, 1024 * 1024 * 1024});
    visit("slow_wait", settings.slowWait, Range{1, 86400});
    visit("transport", settings.transport, Words{transports});
    visit("ip_family", settings.ipFamily, Words{ipFamilies});
    visit("outgoing_rate", settings.outgoingRate, Range{1, 1000});
    visit("dht", settings.dht, Bool{});
    visit("pex", settings.pex, Bool{});
    visit("lsd", settings.lsd, Bool{});
    visit("include_overhead", settings.includesOverhead, Bool{});
    visit("limit_lan", settings.limitsLan, Bool{});
    visit("encryption", settings.encryption, Words{encryptions});
    visit("proxy_type", settings.proxy.type, Words{proxyTypes});
    visit("proxy_host", settings.proxy.host, Text{.valid = host});
    visit("proxy_port", settings.proxy.port, Range{0, 65535});
    visit("proxy_username", settings.proxy.username, Text{.limit = credentialLimit});
    visit("ratio_limit", settings.ratio, Quantity{});
    visit("seeding_minutes", settings.seedingMinutes, Limit{0, INT_MAX});
    visit("check_for_updates", settings.checksUpdates, Bool{});
    visit("schedule_enabled", settings.scheduleEnabled, Bool{});
    visit("torrent_dialog", settings.showsTorrentDialog, Bool{});
    visit("magnet_dialog", settings.showsMagnetDialog, Bool{});
    visit("confirm_delete", settings.confirmsDelete, Bool{});
    visit("delete_downloaded", settings.deletesDownloaded, Bool{});
    visit("delete_torrent_file", settings.deletesTorrentFile, Bool{});
    visit("show_splash", settings.showsSplash, Bool{});
    visit("start_in_tray", settings.startsInTray, Bool{});
    visit("download_limit", settings.limits.download, Limit{0, INT_MAX});
    visit("upload_limit", settings.limits.upload, Limit{0, INT_MAX});
    visit("alternative_download_limit", settings.alternative.download, Limit{0, INT_MAX});
    visit("alternative_upload_limit", settings.alternative.upload, Limit{0, INT_MAX});
    visit("prevent_sleep", settings.preventsSleep, Bool{});
    visit("prevent_sleep_seeding", settings.preventsSleepSeeding, Bool{});
    visit("unfinished_tasks", settings.unfinished, Words{taskAnswers});
}

Json Engine::State::Settings::ToJson() const
{
    auto values = Json::object();
    Fields(*this, [&values](char const* key, auto const& member, auto const& domain)
    {
        if constexpr (requires { domain.Write(member); })
        {
            values[key] = domain.Write(member);
        }
        else
        {
            values[key] = member;
        }
    });
    Json periods = Json::array();
    for (auto const& period : schedule)
    {
        periods.push_back(period.ToJson());
    }
    values[setting::lastFolder] = lastFolder;
    values[setting::additionFolder] = AdditionFolder();
    values[setting::networkInterface] = networkAdapter;
    values[setting::proxyPassword] = proxy.password;
    values[setting::schedule] = std::move(periods);
    values[setting::allPaused] = allPaused;
    values[setting::limitMode] = Name(limitMode);
    values[setting::reportedPrograms] = reportedPrograms;
    return values;
}

Json Engine::State::Settings::ToFile() const
{
    auto values = ToJson();
    values.erase(setting::additionFolder);
    values[setting::proxyPassword] = encryptedPassword;
    return values;
}

// Invalid saved values fall back or clamp to an accepted value, so the person
// never has to repair settings.json; the next save writes the corrected values.
void Engine::State::Settings::Read(Json const& saved)
{
    Fields(*this, [&saved](char const* key, auto& member, auto const& domain)
    {
        auto found = saved.find(key);
        if (found == saved.end())
        {
            return;
        }
        if (domain.Accepts(*found))
        {
            member = domain.From(*found);
        }
        else if constexpr (requires { domain.Nearest(*found); })
        {
            if (auto nearest = domain.Nearest(*found))
            {
                member = *nearest;
            }
        }
    });
    if (auto chosen = ReadSaved(saved, setting::lastFolder, std::string()); IsAbsolute(chosen))
    {
        lastFolder = std::move(chosen);
    }
    watches = watches && IsAbsolute(watchPath);
    // The engine saves an empty folder when Windows has no Downloads folder;
    // a folder it cannot use turns the incomplete folder off.
    auto folder = ReadSaved(saved, setting::incompleteFolder, std::string());
    usesIncompleteFolder = usesIncompleteFolder && (folder.empty() || IsAbsolute(folder)) &&
        IsAbsolute(incompleteFolder);
    // An adapter name that matches no adapter keeps blocking transfers, so it
    // is kept rather than replaced by any adapter.
    networkAdapter = ReadSaved(saved, setting::networkInterface, networkAdapter);
    auto encrypted = ReadSaved(saved, setting::proxyPassword, std::string());
    proxy.password = Unprotect(encrypted);
    // Only a password it could decrypt keeps its encryption, so the file
    // always holds the password the engine uses.
    encryptedPassword = proxy.password.empty() ? std::string() : std::move(encrypted);
    schedule.clear();
    if (auto found = saved.find(setting::schedule); found != saved.end() && found->is_array())
    {
        for (auto const& value : *found)
        {
            if (auto period = Period::Read(value))
            {
                schedule.push_back(std::move(*period));
                if (schedule.size() == periodLimit)
                    break;
            }
        }
    }
    allPaused = ReadSaved(saved, setting::allPaused, allPaused);
    if (auto found = saved.find(setting::limitMode); found != saved.end())
    {
        limitMode = Named(*found).value_or(limitMode);
    }
    if (auto found = saved.find(setting::reportedPrograms); found != saved.end() && found->is_array())
    {
        reportedPrograms.clear();
        for (auto const& program : *found)
        {
            if (program.is_string())
            {
                reportedPrograms.push_back(program.get<std::string>());
            }
        }
    }
}

std::optional<Engine::State::Settings> Engine::State::Settings::With(Json const& changes) const
{
    auto next = *this;
    for (auto const& change : changes.items())
    {
        auto const& key = change.key();
        auto const& value = change.value();
        bool known = false;
        bool accepted = false;
        Fields(next, [&](char const* name, auto& member, auto const& domain)
        {
            if (known || key != name)
            {
                return;
            }
            known = true;
            accepted = domain.Accepts(value);
            if (accepted)
            {
                member = domain.From(value);
            }
        });
        if (known)
        {
            if (!accepted)
            {
                return std::nullopt;
            }
        }
        else if (key == setting::networkInterface && value.is_string())
        {
            auto name = value.get<std::string>();
            if (!name.empty())
            {
                // An adapter name is a GUID in braces. CLSIDFromString also
                // accepts a ProgID, so the form is checked first.
                constexpr std::size_t guidLength = 38;
                if (name.size() != guidLength || name.front() != '{' || name.back() != '}')
                {
                    return std::nullopt;
                }
                CLSID adapter{};
                if (FAILED(CLSIDFromString(Wide(name).c_str(), &adapter)))
                {
                    return std::nullopt;
                }
            }
            next.networkAdapter = std::move(name);
        }
        else if (key == setting::proxyPassword && value.is_string() && value.get<std::string>().size() <= credentialLimit)
        {
            next.proxy.password = value.get<std::string>();
        }
        else if (key == setting::schedule && value.is_array() && value.size() <= periodLimit)
        {
            next.schedule.clear();
            for (auto const& entry : value)
            {
                auto period = Settings::Period::Read(entry);
                if (!period)
                {
                    return std::nullopt;
                }
                next.schedule.push_back(std::move(*period));
            }
        }
        else if (key == setting::limitMode && (value.is_null() || Named(value)))
        {
            if (!value.is_null())
            {
                next.limitMode = *Named(value);
            }
        }
        // The engine records the last folder, session pause and the reported
        // programs itself, so the command refuses them like unknown keys.
        else
        {
            return std::nullopt;
        }
    }
    if (changes.contains(setting::limitMode) && changes.at(setting::limitMode).is_null() && !next.scheduleEnabled)
    {
        return std::nullopt;
    }
    if (next.usesIncompleteFolder && !IsAbsolute(next.incompleteFolder))
    {
        return std::nullopt;
    }
    // A change that leaves the proxy incomplete is refused rather than
    // applied. Other changes still apply to an incomplete proxy that an
    // edited settings.json holds.
    if (next.proxy != proxy && !next.proxy.IsComplete())
    {
        return std::nullopt;
    }
    if (next.watches && !IsAbsolute(next.watchPath))
        return std::nullopt;
    return next;
}

// The changes apply to the settings saved when the change runs, so that
// an earlier settings command still waiting in the queue is kept.
void Engine::State::Configure(Json const& choices, std::function<void(Outcome)> completion)
{
    auto chosen = choices.is_object() && !choices.empty() ? settings.With(choices) : std::nullopt;
    if (!chosen)
    {
        completion({ErrorCode::InvalidRequest});
        return;
    }
    if (!changes.Queue([this, choices, completion]
    {
        auto chosen = settings.With(choices);
        if (!chosen)
        {
            completion({ErrorCode::InvalidRequest});
            return;
        }
        SaveSettings(std::move(*chosen), [this, choices]
        {
            if (choices.contains(setting::limitMode))
            {
                // A change of scheduled mode clears the override, so the mode
                // is brought up to date before the override is set.
                RefreshPolicy();
                limitOverride = settings.scheduleEnabled && !choices.at(setting::limitMode).is_null() ?
                    std::optional<LimitMode>(settings.limitMode) : std::nullopt;
                RefreshPolicy();
            }
        }, completion);
    }))
    {
        completion({ErrorCode::Overloaded});
        return;
    }
    // The chosen language shows at once; `settings` keeps the saved one
    // until the write succeeds.
    if (choices.contains(setting::language))
    {
        language = chosen->language;
    }
}

void Engine::State::TurnOff(std::vector<Setting> const& switches, std::function<void(Outcome)> completion)
{
    auto choices = Json::object();
    for (auto setting : switches)
    {
        choices[Settings::SwitchFor(setting).name] = false;
    }
    Configure(choices, std::move(completion));
}

// Pause all and the reported programs are kept: one is the person's choice
// about transfers rather than a preference, and the other records what the
// person was already told.
void Engine::State::ResetSettings(std::function<void(Outcome)> completion)
{
    if (!changes.Queue([this, completion]
    {
        auto next = Defaults();
        next.allPaused = settings.allPaused;
        next.reportedPrograms = settings.reportedPrograms;
        SaveSettings(std::move(next), [this] { language = settings.language; }, completion);
    }))
    {
        completion({ErrorCode::Overloaded});
    }
}

void Engine::State::SaveSettings(Settings next, std::function<void()> settled, std::function<void(Outcome)> completion)
{
    auto finish = [this, settled, completion]
    {
        settled();
        PublishSnapshot();
        RequestStatus();
        completion({});
    };
    if (next.ToJson() == settings.ToJson())
    {
        finish();
        return;
    }
    if (next.proxy.password != settings.proxy.password)
    {
        auto encrypted = Protect(next.proxy.password);
        if (!encrypted)
        {
            completion(std::move(encrypted.error()));
            return;
        }
        next.encryptedPassword = std::move(*encrypted);
    }
    auto document = Saved();
    document.settings = next;
    changes.Commit([document = std::move(document)] { return document.ToJson(); },
        [this, next, finish, completion](Outcome outcome)
    {
        if (outcome.error)
        {
            completion(std::move(outcome));
            return;
        }
        // The connection test runs only without a proxy or adapter, so choosing
        // either ends it.
        if (connectionTest && (settings.proxy != next.proxy || settings.networkAdapter != next.networkAdapter))
            ReleaseConnectionTest(connectionTest->connectionId, ConnectionPhase::Cancelled);
        if (settings.scheduleEnabled != next.scheduleEnabled)
        {
            limitOverride.reset();
        }
        if (settings.watches != next.watches || settings.watchPath != next.watchPath ||
            settings.watchesRecursively != next.watchesRecursively)
        {
            watchFailed = false;
            watchSaveFailed = false;
        }
        settings = next;
        RefreshPolicy();
        finish();
    });
}

std::string Engine::State::Settings::SavePath(std::string const& destination) const
{
    if (!usesIncompleteFolder || !IsAbsolute(destination) || SameFolder(destination, incompleteFolder))
    {
        return destination;
    }
    return incompleteFolder;
}

std::string const& Engine::State::Settings::AdditionFolder() const
{
    return usesLastFolder && !lastFolder.empty() ? lastFolder : destination;
}

bool Engine::State::IsAbsolute(std::string const& path)
{
    return path.find('\0') == std::string::npos && std::filesystem::path(Wide(path)).is_absolute();
}

void Engine::State::PauseSession(bool paused, std::function<void(Outcome)> completion)
{
    if (!changes.Queue([this, paused, completion]
    {
        RefreshPolicy();
        auto next = settings;
        next.allPaused = paused;
        SaveSettings(std::move(next), [this, paused]
        {
            launchPaused = false;
            bypassesScheduledPause = !paused && ScheduledMode() == ScheduleMode::Paused;
            RefreshPolicy();
            // Pausing the session disconnects every torrent's peers.
            for (auto const& [id, torrent] : torrents)
            {
                Invalidate(id);
            }
        }, completion);
    }))
    {
        completion({ErrorCode::Overloaded});
    }
}

void Engine::State::RecordPrograms(std::vector<std::string> programs, std::function<void(Outcome)> completion)
{
    if (!changes.Queue([this, programs = std::move(programs), completion]
    {
        auto next = settings;
        next.reportedPrograms = programs;
        SaveSettings(std::move(next), [] {}, completion);
    }))
    {
        completion({ErrorCode::Overloaded});
    }
}
}
