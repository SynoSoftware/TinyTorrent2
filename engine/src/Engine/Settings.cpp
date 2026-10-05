#include "Engine/State.h"
#include <Windows.h>
#include <ShlObj.h>
#include "Strings.h"
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
constexpr char usesAlternative[] = "alternative_limits";
constexpr char notificationsEnabled[] = "notifications_enabled";
constexpr char preventSleep[] = "prevent_sleep";
constexpr char preventSleepSeeding[] = "prevent_sleep_seeding";
constexpr char backgroundNoticeShown[] = "background_notice_shown";
}

constexpr std::size_t periodLimit = 128;
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
        {setting::usesAlternative, usesAlternative},
        {setting::notificationsEnabled, notificationsEnabled},
        {setting::preventSleep, preventSleep},
        {setting::preventSleepSeeding, preventSleepSeeding},
        {setting::backgroundNoticeShown, backgroundNoticeShown}};
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
    usesAlternative = saved.value(setting::usesAlternative, usesAlternative);
    notificationsEnabled = saved.value(setting::notificationsEnabled, notificationsEnabled);
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
        else if (key == setting::usesAlternative && value.is_boolean())
        {
            next.usesAlternative = value;
        }
        else if (key == setting::notificationsEnabled && value.is_boolean())
        {
            next.notificationsEnabled = value;
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
        // Validity depends only on the choices, which were checked above.
        auto next = *settings.With(choices);
        auto finish = [this, choices]
        {
            if (choices.contains(setting::usesAlternative))
            {
                // A change of scheduled mode clears the override, so the mode
                // is brought up to date before the override is set.
                RefreshPolicy();
                alternativeOverride = settings.usesAlternative;
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
        changes.Commit(document.ToJson(), reply, [this, next = document.settings, choices, finish]
        {
            settings = next;
            if (choices.contains(setting::schedule) || choices.contains(setting::scheduleEnabled))
            {
                scheduledMode.reset();
            }
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
