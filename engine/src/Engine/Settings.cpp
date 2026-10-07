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
constexpr char destination[] = "default_destination";
constexpr char language[] = "language";
constexpr char theme[] = "theme";
constexpr char portMapping[] = "port_mapping";
constexpr char listenPort[] = "listen_port";
constexpr char networkInterface[] = "network_interface";
constexpr char activeDownloads[] = "active_downloads";
constexpr char activeSeeds[] = "active_seeds";
constexpr char connections[] = "connection_limit";
constexpr char encryption[] = "encryption";
constexpr char proxyType[] = "proxy_type";
constexpr char proxyHost[] = "proxy_host";
constexpr char proxyPort[] = "proxy_port";
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
        {setting::language, language},
        {setting::theme, theme},
        {setting::portMapping, portMapping},
        {setting::listenPort, listenPort},
        {setting::networkInterface, networkInterface},
        {setting::activeDownloads, activeDownloads},
        {setting::activeSeeds, activeSeeds},
        {setting::connections, connections},
        {setting::encryption, Word(encryptions, encryption)},
        {setting::proxyType, Word(proxyTypes, proxy.type)},
        {setting::proxyHost, proxy.host},
        {setting::proxyPort, proxy.port},
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
        {setting::notifyProblems, notifyProblems},
        {setting::notifyAdded, notifyAdded},
        {setting::preventSleep, preventSleep},
        {setting::preventSleepSeeding, preventSleepSeeding},
        {setting::backgroundNoticeShown, backgroundNoticeShown}};
}

Json Engine::State::Settings::ToFile() const
{
    auto values = ToJson();
    values[setting::proxyPassword] = Protect(proxy.password);
    return values;
}

void Engine::State::Settings::Read(Json const& saved)
{
    destination = saved.value(setting::destination, destination);
    language = saved.value(setting::language, language);
    theme = saved.value(setting::theme, theme);
    portMapping = saved.value(setting::portMapping, portMapping);
    listenPort = saved.value(setting::listenPort, listenPort);
    networkInterface = saved.value(setting::networkInterface, networkInterface);
    activeDownloads = saved.value(setting::activeDownloads, activeDownloads);
    activeSeeds = saved.value(setting::activeSeeds, activeSeeds);
    connections = saved.value(setting::connections, connections);
    if (saved.contains(setting::encryption))
    {
        auto chosen = Parse(encryptions, saved.at(setting::encryption).get<std::string>());
        if (!chosen)
        {
            throw std::invalid_argument("Invalid saved encryption");
        }
        encryption = *chosen;
    }
    if (saved.contains(setting::proxyType))
    {
        auto chosen = Parse(proxyTypes, saved.at(setting::proxyType).get<std::string>());
        if (!chosen)
        {
            throw std::invalid_argument("Invalid saved proxy type");
        }
        proxy.type = *chosen;
    }
    proxy.host = saved.value(setting::proxyHost, proxy.host);
    proxy.port = saved.value(setting::proxyPort, proxy.port);
    proxy.username = saved.value(setting::proxyUsername, proxy.username);
    proxy.password = Unprotect(saved.value(setting::proxyPassword, std::string()));
    ratio = saved.value(setting::ratio, ratio);
    seedingMinutes = saved.value(setting::seedingMinutes, seedingMinutes);
    checksUpdates = saved.value(setting::checksUpdates, checksUpdates);
    scheduleEnabled = saved.value(setting::scheduleEnabled, scheduleEnabled);
    schedule.clear();
    for (auto const& value : saved.value(setting::schedule, Json::array()))
    {
        auto period = Period::Read(value);
        if (!period)
        {
            throw std::invalid_argument("Invalid saved schedule period");
        }
        schedule.push_back(std::move(*period));
    }
    showsAdd = saved.value(setting::showsAdd, showsAdd);
    showsSplash = saved.value(setting::showsSplash, showsSplash);
    startsInTray = saved.value(setting::startsInTray, startsInTray);
    allPaused = saved.value(setting::allPaused, allPaused);
    limits.download = saved.value(setting::downloadLimit, limits.download);
    limits.upload = saved.value(setting::uploadLimit, limits.upload);
    alternative.download = saved.value(setting::alternativeDownload, alternative.download);
    alternative.upload = saved.value(setting::alternativeUpload, alternative.upload);
    if (saved.contains(setting::limitMode))
    {
        auto mode = Named(saved.at(setting::limitMode));
        if (!mode)
        {
            throw std::invalid_argument("Invalid saved limit mode");
        }
        limitMode = *mode;
    }
    notificationsEnabled = saved.value(setting::notificationsEnabled, notificationsEnabled);
    notifyProblems = saved.value(setting::notifyProblems, notifyProblems);
    notifyAdded = saved.value(setting::notifyAdded, notifyAdded);
    preventSleep = saved.value(setting::preventSleep, preventSleep);
    preventSleepSeeding = saved.value(setting::preventSleepSeeding, preventSleepSeeding);
    backgroundNoticeShown = saved.value(setting::backgroundNoticeShown, backgroundNoticeShown);
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
        else if (key == setting::theme && (value == "system" || value == "light" || value == "dark"))
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
            next.portMapping = value;
        }
        else if (key == setting::listenPort && isLimit && value >= 1 && value <= 65535)
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
            next.networkInterface = std::move(name);
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
        else if (key == setting::proxyPort && value.is_number_integer() && value >= 0 && value <= 65535)
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
        else if (key == setting::ratio && value.is_number() && value >= 0 &&
            std::isfinite(value.get<double>()))
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
            next.notifyProblems = value;
        }
        else if (key == setting::notifyAdded && value.is_boolean())
        {
            next.notifyAdded = value;
        }
        else if (key == setting::preventSleep && value.is_boolean())
        {
            next.preventSleep = value;
        }
        else if (key == setting::preventSleepSeeding && value.is_boolean())
        {
            next.preventSleepSeeding = value;
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

bool Engine::State::IsAbsolute(std::string const& path)
{
    return std::filesystem::path(Wide(path)).is_absolute();
}

void Engine::State::PauseSession(bool paused, std::function<void(Outcome)> done)
{
    if (!changes.Queue([this, paused, done]
    {
        RefreshPolicy();
        auto finish = [this, paused, done]
        {
            bypassesScheduledPause = !paused && ScheduledMode() == ScheduleMode::Paused;
            RefreshPolicy();
            done({});
        };
        if (settings.allPaused == paused)
        {
            finish();
            return;
        }
        auto document = Saved();
        document.settings.allPaused = paused;
        changes.Commit(document.ToJson(), [this, paused, finish, done](StorageOutcome outcome)
        {
            if (!outcome.succeeded)
            {
                done({ErrorCode::StorageFailed, outcome.detail});
                return;
            }
            settings.allPaused = paused;
            finish();
        });
    }))
    {
        done({ErrorCode::Overloaded});
    }
}

void Engine::State::RecordBackgroundNotice(std::function<void(Outcome)> done)
{
    if (!changes.Queue([this, done]
    {
        if (settings.backgroundNoticeShown)
        {
            done({});
            return;
        }
        auto document = Saved();
        document.settings.backgroundNoticeShown = true;
        changes.Commit(document.ToJson(), [this, done](StorageOutcome outcome)
        {
            if (!outcome.succeeded)
            {
                done({ErrorCode::StorageFailed, outcome.detail});
                return;
            }
            settings.backgroundNoticeShown = true;
            done({});
        });
    }))
    {
        done({ErrorCode::Overloaded});
    }
}
}
