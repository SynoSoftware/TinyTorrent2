#include "Engine/State.h"
#include <Windows.h>
#include <ShlObj.h>
#include <wincrypt.h>
#include "Strings.h"
#include <algorithm>
#include <climits>
#include <cmath>

namespace tt
{
namespace
{
// The key of each setting in settings.json and in the settings command and
// reply. A key is file format: renaming one needs a migration.
namespace setting
{
// A whole-number setting and the values it accepts.
struct Number
{
    char const* key;
    int minimum;
    int maximum;

    bool Accepts(Json const& value) const
    {
        return value.is_number_integer() && value >= minimum && value <= maximum;
    }

    // A saved number outside the range takes the nearest value it accepts.
    int Read(Json const& saved, int fallback) const
    {
        auto found = saved.find(key);
        if (found == saved.end() || !found->is_number_integer())
        {
            return fallback;
        }
        if (*found < minimum)
        {
            return minimum;
        }
        if (*found > maximum)
        {
            return maximum;
        }
        return found->get<int>();
    }
};

constexpr char destination[] = "default_destination";
constexpr char incompleteFolder[] = "incomplete_folder";
constexpr char usesIncompleteFolder[] = "use_incomplete_folder";
constexpr char appendsSuffix[] = "append_suffix";
constexpr char confirmsExit[] = "confirm_exit";
constexpr char showsExternalIp[] = "show_external_ip";
constexpr Number diskBuffer{"disk_buffer_mib", 1, 1024};
constexpr Number checkingMemory{"checking_memory_mib", 1, 1024};
constexpr Number hashingThreads{"hashing_threads", 1, 64};
constexpr Number filePool{"file_pool_size", 1, 10000};
constexpr char language[] = "language";
constexpr char theme[] = "theme";
constexpr char portMapping[] = "port_mapping";
constexpr Number listenPort{"listen_port", 1, 65535};
constexpr char networkInterface[] = "network_interface";
constexpr char activeDownloads[] = "active_downloads";
constexpr char activeSeeds[] = "active_seeds";
constexpr char connections[] = "connection_limit";
constexpr char encryption[] = "encryption";
constexpr char proxyType[] = "proxy_type";
constexpr char proxyHost[] = "proxy_host";
constexpr Number proxyPort{"proxy_port", 0, 65535};
constexpr char proxyUsername[] = "proxy_username";
constexpr char proxyPassword[] = "proxy_password";
constexpr char ratio[] = "ratio_limit";
constexpr char seedingMinutes[] = "seeding_minutes";
constexpr char checksUpdates[] = "check_for_updates";
constexpr char scheduleEnabled[] = "schedule_enabled";
constexpr char schedule[] = "schedule";
constexpr char showsAdd[] = "show_add";
constexpr char showsSplash[] = "show_splash";
constexpr char startsInTray[] = "start_in_tray";
constexpr char allPaused[] = "all_paused";
constexpr char downloadLimit[] = "download_limit";
constexpr char uploadLimit[] = "upload_limit";
constexpr char alternativeDownload[] = "alternative_download_limit";
constexpr char alternativeUpload[] = "alternative_upload_limit";
constexpr char limitMode[] = "limit_mode";
constexpr char notificationsEnabled[] = "notifications_enabled";
constexpr char notifyProblems[] = "notify_problems";
constexpr char notifyAdded[] = "notify_added";
constexpr char preventSleep[] = "prevent_sleep";
constexpr char preventSleepSeeding[] = "prevent_sleep_seeding";
constexpr char backgroundNoticeShown[] = "background_notice_shown";
constexpr char reportedPrograms[] = "reported_programs";
}

constexpr std::size_t periodLimit = 128;
// SOCKS5 sends the user name and the password each after a one-byte length.
constexpr std::size_t credentialLimit = 255;
// The longest DNS name.
constexpr std::size_t hostLimit = 253;

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

// The password is encrypted for the current Windows user, so a copy of
// settings.json does not reveal it. When encryption fails, the file holds no
// password, because every save writes the settings and an exception would end
// the engine in the middle of one.
std::string Protect(std::string const& secret)
{
    if (secret.empty())
    {
        return {};
    }
    DATA_BLOB input{static_cast<DWORD>(secret.size()), reinterpret_cast<BYTE*>(const_cast<char*>(secret.data()))};
    DATA_BLOB output{};
    if (!CryptProtectData(&input, nullptr, nullptr, nullptr, nullptr, CRYPTPROTECT_UI_FORBIDDEN, &output))
    {
        return {};
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

bool IsTheme(std::string const& theme)
{
    return theme == "system" || theme == "light" || theme == "dark";
}

bool IsRatio(Json const& value)
{
    return value.is_number() && value >= 0 && std::isfinite(value.get<double>());
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

Json Engine::State::Settings::ToJson() const
{
    Json periods = Json::array();
    for (auto const& period : schedule)
    {
        periods.push_back(period.ToJson());
    }
    return {
        {setting::destination, destination},
        {setting::incompleteFolder, incompleteFolder},
        {setting::usesIncompleteFolder, usesIncompleteFolder},
        {setting::appendsSuffix, appendsSuffix},
        {setting::confirmsExit, confirmsExit},
        {setting::showsExternalIp, showsExternalIp},
        {setting::diskBuffer.key, diskBufferMib},
        {setting::checkingMemory.key, checkingMib},
        {setting::hashingThreads.key, hashingThreads},
        {setting::filePool.key, fileLimit},
        {setting::language, language},
        {setting::theme, theme},
        {setting::portMapping, mapsPorts},
        {setting::listenPort.key, listenPort},
        {setting::networkInterface, networkAdapter},
        {setting::activeDownloads, activeDownloads},
        {setting::activeSeeds, activeSeeds},
        {setting::connections, connections},
        {setting::encryption, Word(encryptions, encryption)},
        {setting::proxyType, Word(proxyTypes, proxy.type)},
        {setting::proxyHost, proxy.host},
        {setting::proxyPort.key, proxy.port},
        {setting::proxyUsername, proxy.username},
        {setting::proxyPassword, proxy.password},
        {setting::ratio, ratio},
        {setting::seedingMinutes, seedingMinutes},
        {setting::checksUpdates, checksUpdates},
        {setting::scheduleEnabled, scheduleEnabled},
        {setting::schedule, std::move(periods)},
        {setting::showsAdd, showsAdd},
        {setting::showsSplash, showsSplash},
        {setting::startsInTray, startsInTray},
        {setting::allPaused, allPaused},
        {setting::downloadLimit, limits.download},
        {setting::uploadLimit, limits.upload},
        {setting::alternativeDownload, alternative.download},
        {setting::alternativeUpload, alternative.upload},
        {setting::limitMode, Name(limitMode)},
        {setting::notificationsEnabled, notificationsEnabled},
        {setting::notifyProblems, notifiesProblems},
        {setting::notifyAdded, notifiesAdded},
        {setting::preventSleep, preventsSleep},
        {setting::preventSleepSeeding, preventsSleepSeeding},
        {setting::backgroundNoticeShown, backgroundNoticeShown},
        {setting::reportedPrograms, reportedPrograms}};
}

Json Engine::State::Settings::ToFile() const
{
    auto values = ToJson();
    values[setting::proxyPassword] = Protect(proxy.password);
    return values;
}

// A value that settings.json holds incorrectly keeps its default, so the
// person never has to repair the file; the next save writes the default.
void Engine::State::Settings::Read(Json const& saved)
{
    if (auto chosen = ReadSaved(saved, setting::destination, std::string()); IsAbsolute(chosen))
    {
        destination = std::move(chosen);
    }
    // The engine saves an empty folder when Windows has no Downloads folder;
    // a folder it cannot use turns the incomplete folder off.
    auto folder = ReadSaved(saved, setting::incompleteFolder, std::string());
    if (IsAbsolute(folder))
    {
        incompleteFolder = folder;
    }
    usesIncompleteFolder = ReadSaved(saved, setting::usesIncompleteFolder, usesIncompleteFolder) &&
        (folder.empty() || IsAbsolute(folder)) && IsAbsolute(incompleteFolder);
    appendsSuffix = ReadSaved(saved, setting::appendsSuffix, appendsSuffix);
    confirmsExit = ReadSaved(saved, setting::confirmsExit, confirmsExit);
    showsExternalIp = ReadSaved(saved, setting::showsExternalIp, showsExternalIp);
    diskBufferMib = setting::diskBuffer.Read(saved, diskBufferMib);
    checkingMib = setting::checkingMemory.Read(saved, checkingMib);
    hashingThreads = setting::hashingThreads.Read(saved, hashingThreads);
    fileLimit = setting::filePool.Read(saved, fileLimit);
    if (auto chosen = ReadSaved(saved, setting::language, std::string()); Strings::Supports(chosen))
    {
        language = std::move(chosen);
    }
    if (auto chosen = ReadSaved(saved, setting::theme, std::string()); IsTheme(chosen))
    {
        theme = std::move(chosen);
    }
    mapsPorts = ReadSaved(saved, setting::portMapping, mapsPorts);
    listenPort = setting::listenPort.Read(saved, listenPort);
    // An adapter name that matches no adapter keeps blocking transfers, so it
    // is kept rather than replaced by any adapter.
    networkAdapter = ReadSaved(saved, setting::networkInterface, networkAdapter);
    activeDownloads = ReadSaved(saved, setting::activeDownloads, activeDownloads);
    activeSeeds = ReadSaved(saved, setting::activeSeeds, activeSeeds);
    connections = ReadSaved(saved, setting::connections, connections);
    if (auto chosen = Parse(encryptions, ReadSaved(saved, setting::encryption, std::string())))
    {
        encryption = *chosen;
    }
    if (auto chosen = Parse(proxyTypes, ReadSaved(saved, setting::proxyType, std::string())))
    {
        proxy.type = *chosen;
    }
    if (auto host = ReadSaved(saved, setting::proxyHost, std::string()); IsHost(host))
    {
        proxy.host = std::move(host);
    }
    proxy.port = setting::proxyPort.Read(saved, proxy.port);
    if (auto name = ReadSaved(saved, setting::proxyUsername, std::string()); name.size() <= credentialLimit)
    {
        proxy.username = std::move(name);
    }
    proxy.password = Unprotect(ReadSaved(saved, setting::proxyPassword, std::string()));
    if (auto found = saved.find(setting::ratio); found != saved.end() && IsRatio(*found))
    {
        ratio = found->get<double>();
    }
    seedingMinutes = ReadSaved(saved, setting::seedingMinutes, seedingMinutes);
    checksUpdates = ReadSaved(saved, setting::checksUpdates, checksUpdates);
    scheduleEnabled = ReadSaved(saved, setting::scheduleEnabled, scheduleEnabled);
    schedule.clear();
    if (auto found = saved.find(setting::schedule); found != saved.end() && found->is_array())
    {
        for (auto const& value : *found)
        {
            if (auto period = Period::Read(value))
            {
                schedule.push_back(std::move(*period));
            }
        }
    }
    showsAdd = ReadSaved(saved, setting::showsAdd, showsAdd);
    showsSplash = ReadSaved(saved, setting::showsSplash, showsSplash);
    startsInTray = ReadSaved(saved, setting::startsInTray, startsInTray);
    allPaused = ReadSaved(saved, setting::allPaused, allPaused);
    limits.download = ReadSaved(saved, setting::downloadLimit, limits.download);
    limits.upload = ReadSaved(saved, setting::uploadLimit, limits.upload);
    alternative.download = ReadSaved(saved, setting::alternativeDownload, alternative.download);
    alternative.upload = ReadSaved(saved, setting::alternativeUpload, alternative.upload);
    if (auto found = saved.find(setting::limitMode); found != saved.end())
    {
        limitMode = Named(*found).value_or(limitMode);
    }
    notificationsEnabled = ReadSaved(saved, setting::notificationsEnabled, notificationsEnabled);
    notifiesProblems = ReadSaved(saved, setting::notifyProblems, notifiesProblems);
    notifiesAdded = ReadSaved(saved, setting::notifyAdded, notifiesAdded);
    preventsSleep = ReadSaved(saved, setting::preventSleep, preventsSleep);
    preventsSleepSeeding = ReadSaved(saved, setting::preventSleepSeeding, preventsSleepSeeding);
    backgroundNoticeShown = ReadSaved(saved, setting::backgroundNoticeShown, backgroundNoticeShown);
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
    for (auto const& [key, value] : changes.items())
    {
        bool isLimit = value.is_number_integer() && value >= 0 && value <= INT_MAX;
        if (key == setting::language && value.is_string() && Strings::Supports(value.get<std::string>()))
        {
            next.language = value.get<std::string>();
        }
        else if (key == setting::theme && value.is_string() && IsTheme(value.get<std::string>()))
        {
            next.theme = value.get<std::string>();
        }
        else if (key == setting::destination && value.is_string() && IsAbsolute(value.get<std::string>()))
        {
            next.destination = value.get<std::string>();
        }
        else if (key == setting::showsAdd && value.is_boolean())
        {
            next.showsAdd = value;
        }
        else if (key == setting::incompleteFolder && value.is_string() && IsAbsolute(value.get<std::string>()))
        {
            next.incompleteFolder = value.get<std::string>();
        }
        else if (key == setting::usesIncompleteFolder && value.is_boolean())
        {
            next.usesIncompleteFolder = value;
        }
        else if (key == setting::appendsSuffix && value.is_boolean())
        {
            next.appendsSuffix = value;
        }
        else if (key == setting::confirmsExit && value.is_boolean())
        {
            next.confirmsExit = value;
        }
        else if (key == setting::showsExternalIp && value.is_boolean())
        {
            next.showsExternalIp = value;
        }
        else if (key == setting::diskBuffer.key && setting::diskBuffer.Accepts(value))
        {
            next.diskBufferMib = value;
        }
        else if (key == setting::checkingMemory.key && setting::checkingMemory.Accepts(value))
        {
            next.checkingMib = value;
        }
        else if (key == setting::hashingThreads.key && setting::hashingThreads.Accepts(value))
        {
            next.hashingThreads = value;
        }
        else if (key == setting::filePool.key && setting::filePool.Accepts(value))
        {
            next.fileLimit = value;
        }
        else if (key == setting::showsSplash && value.is_boolean())
        {
            next.showsSplash = value;
        }
        else if (key == setting::startsInTray && value.is_boolean())
        {
            next.startsInTray = value;
        }
        else if (key == setting::portMapping && value.is_boolean())
        {
            next.mapsPorts = value;
        }
        else if (key == setting::listenPort.key && setting::listenPort.Accepts(value))
        {
            next.listenPort = value;
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
        else if (key == setting::activeDownloads && isLimit)
        {
            next.activeDownloads = value;
        }
        else if (key == setting::activeSeeds && isLimit)
        {
            next.activeSeeds = value;
        }
        else if (key == setting::connections && isLimit)
        {
            next.connections = value;
        }
        else if (key == setting::encryption && value.is_string() && Parse(encryptions, value.get<std::string>()))
        {
            next.encryption = *Parse(encryptions, value.get<std::string>());
        }
        else if (key == setting::proxyType && value.is_string() && Parse(proxyTypes, value.get<std::string>()))
        {
            next.proxy.type = *Parse(proxyTypes, value.get<std::string>());
        }
        else if (key == setting::proxyHost && value.is_string() && (value == "" || IsHost(value.get<std::string>())))
        {
            next.proxy.host = value.get<std::string>();
        }
        else if (key == setting::proxyPort.key && setting::proxyPort.Accepts(value))
        {
            next.proxy.port = value;
        }
        else if (key == setting::proxyUsername && value.is_string() && value.get<std::string>().size() <= credentialLimit)
        {
            next.proxy.username = value.get<std::string>();
        }
        else if (key == setting::proxyPassword && value.is_string() && value.get<std::string>().size() <= credentialLimit)
        {
            next.proxy.password = value.get<std::string>();
        }
        else if (key == setting::ratio && IsRatio(value))
        {
            next.ratio = value;
        }
        else if (key == setting::seedingMinutes && isLimit)
        {
            next.seedingMinutes = value;
        }
        else if (key == setting::checksUpdates && value.is_boolean())
        {
            next.checksUpdates = value;
        }
        else if (key == setting::scheduleEnabled && value.is_boolean())
        {
            next.scheduleEnabled = value;
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
        else if (key == setting::notificationsEnabled && value.is_boolean())
        {
            next.notificationsEnabled = value;
        }
        else if (key == setting::notifyProblems && value.is_boolean())
        {
            next.notifiesProblems = value;
        }
        else if (key == setting::notifyAdded && value.is_boolean())
        {
            next.notifiesAdded = value;
        }
        else if (key == setting::preventSleep && value.is_boolean())
        {
            next.preventsSleep = value;
        }
        else if (key == setting::preventSleepSeeding && value.is_boolean())
        {
            next.preventsSleepSeeding = value;
        }
        else if (key == setting::downloadLimit && isLimit)
        {
            next.limits.download = value;
        }
        else if (key == setting::uploadLimit && isLimit)
        {
            next.limits.upload = value;
        }
        else if (key == setting::alternativeDownload && isLimit)
        {
            next.alternative.download = value;
        }
        else if (key == setting::alternativeUpload && isLimit)
        {
            next.alternative.upload = value;
        }
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
    // A proxy works only with its address, so a change that leaves it
    // half-made is refused rather than applied. Other changes still apply to
    // a half-made proxy that an edited settings.json holds.
    if (next.proxy != proxy && next.proxy.type != ProxyType::None &&
        (next.proxy.host.empty() || next.proxy.port == 0))
    {
        return std::nullopt;
    }
    return next;
}

// The changes apply to the settings saved when the change runs, so that
// an earlier settings command still waiting in the queue is kept.
void Engine::State::Configure(Json const& choices, Reply reply)
{
    auto chosen = choices.is_object() && !choices.empty() ? settings.With(choices) : std::nullopt;
    if (!chosen)
    {
        reply(Failure(ErrorCode::InvalidRequest));
        return;
    }
    if (!changes.Queue([this, choices, reply]
    {
        auto chosen = settings.With(choices);
        if (!chosen)
        {
            reply(Failure(ErrorCode::InvalidRequest));
            return;
        }
        auto next = *chosen;
        auto finish = [this, choices]
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
            return Success(settings.ToJson());
        };
        if (next.ToJson() == settings.ToJson())
        {
            reply(finish());
            return;
        }
        auto document = Saved();
        document.settings = next;
        changes.Commit(document.ToJson(), reply, [this, next = document.settings, finish]
        {
            if (settings.scheduleEnabled != next.scheduleEnabled)
            {
                limitOverride.reset();
            }
            settings = next;
            RefreshPolicy(true);
            return finish();
        });
    }))
    {
        reply(Failure(ErrorCode::Overloaded));
        return;
    }
    // The chosen language shows at once; `settings` keeps the saved one
    // until the write succeeds.
    if (choices.contains(setting::language))
    {
        language = chosen->language;
    }
}

std::string Engine::State::Settings::SavePath(std::string const& destination) const
{
    if (!usesIncompleteFolder || !IsAbsolute(destination) || SameFolder(destination, incompleteFolder))
    {
        return destination;
    }
    return incompleteFolder;
}

bool Engine::State::IsAbsolute(std::string const& path)
{
    return std::filesystem::path(Wide(path)).is_absolute();
}

void Engine::State::PauseSession(bool paused, std::function<void(Outcome)> completion)
{
    if (!changes.Queue([this, paused, completion]
    {
        RefreshPolicy();
        auto finish = [this, paused, completion]
        {
            bypassesScheduledPause = !paused && ScheduledMode() == ScheduleMode::Paused;
            RefreshPolicy();
            completion({});
        };
        if (settings.allPaused == paused)
        {
            finish();
            return;
        }
        auto document = Saved();
        document.settings.allPaused = paused;
        changes.Commit(document.ToJson(), [this, paused, finish, completion](StorageOutcome outcome)
        {
            if (!outcome.succeeded)
            {
                completion({ErrorCode::StorageFailed, outcome.detail});
                return;
            }
            settings.allPaused = paused;
            finish();
        });
    }))
    {
        completion({ErrorCode::Overloaded});
    }
}

void Engine::State::RecordBackgroundNotice(std::function<void(Outcome)> completion)
{
    if (!changes.Queue([this, completion]
    {
        if (settings.backgroundNoticeShown)
        {
            completion({});
            return;
        }
        auto document = Saved();
        document.settings.backgroundNoticeShown = true;
        changes.Commit(document.ToJson(), [this, completion](StorageOutcome outcome)
        {
            if (!outcome.succeeded)
            {
                completion({ErrorCode::StorageFailed, outcome.detail});
                return;
            }
            settings.backgroundNoticeShown = true;
            completion({});
        });
    }))
    {
        completion({ErrorCode::Overloaded});
    }
}

void Engine::State::RecordPrograms(std::vector<std::string> programs, std::function<void(Outcome)> completion)
{
    if (!changes.Queue([this, programs = std::move(programs), completion]
    {
        auto document = Saved();
        document.settings.reportedPrograms = programs;
        changes.Commit(document.ToJson(), [this, programs, completion](StorageOutcome outcome)
        {
            if (!outcome.succeeded)
            {
                completion({ErrorCode::StorageFailed, outcome.detail});
                return;
            }
            settings.reportedPrograms = programs;
            completion({});
        });
    }))
    {
        completion({ErrorCode::Overloaded});
    }
}
}
