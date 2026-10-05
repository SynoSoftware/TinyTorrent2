#include "Engine/State.h"
#include <Windows.h>
#include <ShlObj.h>
#include "Strings.h"
#include <climits>
#include <cmath>

namespace tiny
{
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
        {"default_destination", destination},
        {"language", language},
        {"theme", theme},
        {"port_mapping", portMapping},
        {"listen_port", listenPort},
        {"network_interface", networkInterface},
        {"active_downloads", activeDownloads},
        {"active_seeds", activeSeeds},
        {"connection_limit", connections},
        {"ratio_limit", ratio},
        {"seeding_minutes", seedingMinutes},
        {"check_for_updates", checksUpdates},
        {"schedule_enabled", scheduleEnabled},
        {"schedule", std::move(periods)},
        {"show_add", showsAdd},
        {"all_paused", allPaused},
        {"download_limit", limits.download},
        {"upload_limit", limits.upload},
        {"alternative_download_limit", alternative.download},
        {"alternative_upload_limit", alternative.upload},
        {"alternative_limits", usesAlternative},
        {"notifications_enabled", notificationsEnabled},
        {"prevent_sleep", preventSleep},
        {"prevent_sleep_seeding", preventSleepSeeding},
        {"background_notice_shown", backgroundNoticeShown}};
}

void Engine::State::Settings::Read(Json const& saved)
{
    destination = saved.value("default_destination", destination);
    language = saved.value("language", language);
    theme = saved.value("theme", theme);
    portMapping = saved.value("port_mapping", portMapping);
    listenPort = saved.value("listen_port", listenPort);
    networkInterface = saved.value("network_interface", networkInterface);
    activeDownloads = saved.value("active_downloads", activeDownloads);
    activeSeeds = saved.value("active_seeds", activeSeeds);
    connections = saved.value("connection_limit", connections);
    ratio = saved.value("ratio_limit", ratio);
    seedingMinutes = saved.value("seeding_minutes", seedingMinutes);
    checksUpdates = saved.value("check_for_updates", checksUpdates);
    scheduleEnabled = saved.value("schedule_enabled", scheduleEnabled);
    schedule.clear();
    for (auto const& value : saved.value("schedule", Json::array()))
    {
        auto period = Period::Read(value);
        if (!period)
        {
            throw std::invalid_argument("Invalid saved schedule period");
        }
        schedule.push_back(std::move(*period));
    }
    showsAdd = saved.value("show_add", showsAdd);
    allPaused = saved.value("all_paused", allPaused);
    limits.download = saved.value("download_limit", limits.download);
    limits.upload = saved.value("upload_limit", limits.upload);
    alternative.download = saved.value("alternative_download_limit", alternative.download);
    alternative.upload = saved.value("alternative_upload_limit", alternative.upload);
    usesAlternative = saved.value("alternative_limits", usesAlternative);
    notificationsEnabled = saved.value("notifications_enabled", notificationsEnabled);
    preventSleep = saved.value("prevent_sleep", preventSleep);
    preventSleepSeeding = saved.value("prevent_sleep_seeding", preventSleepSeeding);
    backgroundNoticeShown = saved.value("background_notice_shown", backgroundNoticeShown);
}

std::optional<Engine::State::Settings> Engine::State::Settings::With(Json const& changes) const
{
    auto next = *this;
    for (auto const& [key, value] : changes.items())
    {
        bool isLimit = value.is_number_integer() && value >= 0 && value <= INT_MAX;
        if (key == "language" && (value == "en" || value == "es"))
        {
            next.language = value.get<std::string>();
        }
        else if (key == "theme" && (value == "system" || value == "light" || value == "dark"))
        {
            next.theme = value.get<std::string>();
        }
        else if (key == "default_destination" && value.is_string() && IsAbsolute(value.get<std::string>()))
        {
            next.destination = value.get<std::string>();
        }
        else if (key == "show_add" && value.is_boolean())
        {
            next.showsAdd = value;
        }
        else if (key == "port_mapping" && value.is_boolean())
        {
            next.portMapping = value;
        }
        else if (key == "listen_port" && isLimit && value >= 1 && value <= 65535)
        {
            next.listenPort = value;
        }
        else if (key == "network_interface" && value.is_string())
        {
            auto name = value.get<std::string>();
            if (!name.empty())
            {
                if (name.size() != 38 || name.front() != '{' || name.back() != '}')
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
        else if (key == "active_downloads" && isLimit)
        {
            next.activeDownloads = value;
        }
        else if (key == "active_seeds" && isLimit)
        {
            next.activeSeeds = value;
        }
        else if (key == "connection_limit" && isLimit)
        {
            next.connections = value;
        }
        else if (key == "ratio_limit" && value.is_number() && value >= 0 &&
            std::isfinite(value.get<double>()))
        {
            next.ratio = value;
        }
        else if (key == "seeding_minutes" && isLimit)
        {
            next.seedingMinutes = value;
        }
        else if (key == "check_for_updates" && value.is_boolean())
        {
            next.checksUpdates = value;
        }
        else if (key == "schedule_enabled" && value.is_boolean())
        {
            next.scheduleEnabled = value;
        }
        else if (key == "schedule" && value.is_array() && value.size() <= 128)
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
        else if (key == "alternative_limits" && value.is_boolean())
        {
            next.usesAlternative = value;
        }
        else if (key == "notifications_enabled" && value.is_boolean())
        {
            next.notificationsEnabled = value;
        }
        else if (key == "prevent_sleep" && value.is_boolean())
        {
            next.preventSleep = value;
        }
        else if (key == "prevent_sleep_seeding" && value.is_boolean())
        {
            next.preventSleepSeeding = value;
        }
        else if (key == "background_notice_shown" && value.is_boolean())
        {
            next.backgroundNoticeShown = value;
        }
        else if (key == "download_limit" && isLimit)
        {
            next.limits.download = value;
        }
        else if (key == "upload_limit" && isLimit)
        {
            next.limits.upload = value;
        }
        else if (key == "alternative_download_limit" && isLimit)
        {
            next.alternative.download = value;
        }
        else if (key == "alternative_upload_limit" && isLimit)
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
        reply(Failure("invalid_request"));
        return;
    }
    if (!changes.Queue([this, choices, reply]
    {
        // Validity depends only on the choices, which were checked above.
        auto next = *settings.With(choices);
        if (next.ToJson() == settings.ToJson())
        {
            if (choices.contains("alternative_limits"))
            {
                RefreshPolicy();
                alternativeOverride = next.usesAlternative;
                RefreshPolicy();
            }
            reply(Success(settings.ToJson()));
            return;
        }
        auto document = Saved();
        document.settings = next;
        changes.Commit(document.ToJson(), reply, [this, next = document.settings, choices]
        {
            settings = next;
            if (choices.contains("schedule") || choices.contains("schedule_enabled"))
            {
                scheduledMode.reset();
            }
            RefreshPolicy(true);
            if (choices.contains("alternative_limits"))
            {
                alternativeOverride = settings.usesAlternative;
                RefreshPolicy();
            }
            return Success(settings.ToJson());
        });
    }))
    {
        reply(Failure("overloaded"));
        return;
    }
    // The chosen language shows at once; `settings` keeps the saved one
    // until the write succeeds.
    if (choices.contains("language"))
    {
        language = chosen->language;
    }
}

bool Engine::State::IsAbsolute(std::string const& path)
{
    return std::filesystem::path(Wide(path)).is_absolute();
}

void Engine::State::PauseSession(bool paused, Reply reply)
{
    if (!changes.Queue([this, paused, reply]
    {
        RefreshPolicy();
        if (settings.allPaused == paused)
        {
            bypassesScheduledPause = !paused && ScheduledMode() == ScheduleMode::Paused;
            RefreshPolicy();
            reply(Success());
            return;
        }
        auto document = Saved();
        document.settings.allPaused = paused;
        changes.Commit(document.ToJson(), reply, [this, paused]
        {
            settings.allPaused = paused;
            bypassesScheduledPause = !paused && ScheduledMode() == ScheduleMode::Paused;
            RefreshPolicy();
            return Success();
        });
    }))
    {
        reply(Failure("overloaded"));
    }
}
}
