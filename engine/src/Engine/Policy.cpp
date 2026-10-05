#include "Engine/State.h"
#include <winsock2.h>
#include <iphlpapi.h>
#include <libtorrent/settings_pack.hpp>
#include <algorithm>
#include <cctype>
#include <climits>

namespace tiny
{
namespace
{
bool InterfaceAvailable(std::string const& name)
{
    if (name.empty())
    {
        return true;
    }
    ULONG size = 15 * 1024;
    std::vector<char> buffer(size);
    auto read = [&]
    {
        return GetAdaptersAddresses(AF_UNSPEC, GAA_FLAG_SKIP_ANYCAST |
            GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_DNS_SERVER, nullptr,
            reinterpret_cast<IP_ADAPTER_ADDRESSES*>(buffer.data()), &size);
    };
    auto outcome = read();
    if (outcome == ERROR_BUFFER_OVERFLOW && size <= 1024 * 1024)
    {
        buffer.resize(size);
        outcome = read();
    }
    if (outcome != NO_ERROR)
    {
        return false;
    }
    for (auto adapter = reinterpret_cast<IP_ADAPTER_ADDRESSES*>(buffer.data());
        adapter; adapter = adapter->Next)
    {
        if (_stricmp(adapter->AdapterName, name.c_str()) == 0)
        {
            return adapter->OperStatus == IfOperStatusUp && adapter->FirstUnicastAddress;
        }
    }
    return false;
}

std::string AdapterName(std::string name)
{
    std::transform(name.begin(), name.end(), name.begin(),
        [](unsigned char character) { return static_cast<char>(std::toupper(character)); });
    return name;
}
}

bool Engine::State::Settings::Period::Contains(int day, int minute) const
{
    auto startsToday = std::find(days.begin(), days.end(), day) != days.end();
    if (end > start)
    {
        return startsToday && minute >= start && minute < end;
    }
    auto startedYesterday = std::find(days.begin(), days.end(), (day + 6) % 7) != days.end();
    return (startsToday && minute >= start) || (startedYesterday && minute < end);
}

Json Engine::State::Settings::Period::ToJson() const
{
    return {{"days", days}, {"start", start}, {"end", end},
        {"mode", mode == ScheduleMode::Paused ? "paused" : "alternative"}};
}

std::optional<Engine::State::Settings::Period> Engine::State::Settings::Period::Read(Json const& value)
{
    if (!value.is_object() || !value.contains("days") || !value.at("days").is_array() ||
        value.at("days").empty() || value.at("days").size() > 7)
    {
        return std::nullopt;
    }
    for (auto key : {"start", "end"})
    {
        if (!value.contains(key) || !value.at(key).is_number_integer() ||
            value.at(key) < 0 || value.at(key) >= 1440)
        {
            return std::nullopt;
        }
    }
    if (!value.contains("mode") || (value.at("mode") != "paused" && value.at("mode") != "alternative"))
    {
        return std::nullopt;
    }
    Period period;
    period.start = value.at("start");
    period.end = value.at("end");
    period.mode = value.at("mode") == "paused" ? ScheduleMode::Paused : ScheduleMode::Alternative;
    for (auto const& day : value.at("days"))
    {
        if (!day.is_number_integer() || day < 0 || day > 6 ||
            std::find(period.days.begin(), period.days.end(), day.get<int>()) != period.days.end())
        {
            return std::nullopt;
        }
        period.days.push_back(day.get<int>());
    }
    return period;
}

ScheduleMode Engine::State::ScheduledMode() const
{
    if (!settings.scheduleEnabled)
    {
        return ScheduleMode::Normal;
    }
    SYSTEMTIME time;
    GetLocalTime(&time);
    auto mode = ScheduleMode::Normal;
    for (auto const& period : settings.schedule)
    {
        if (!period.Contains((time.wDayOfWeek + 6) % 7, time.wHour * 60 + time.wMinute))
        {
            continue;
        }
        if (period.mode == ScheduleMode::Paused)
        {
            return ScheduleMode::Paused;
        }
        mode = ScheduleMode::Alternative;
    }
    return mode;
}

bool Engine::State::IsPaused() const
{
    return settings.allPaused || interfaceMissing ||
        (scheduledMode == ScheduleMode::Paused && !bypassesScheduledPause);
}

bool Engine::State::UsesAlternative() const
{
    if (alternativeOverride)
    {
        return *alternativeOverride;
    }
    return settings.scheduleEnabled ? scheduledMode == ScheduleMode::Alternative : settings.usesAlternative;
}

void Engine::State::RefreshPolicy(bool configure)
{
    auto mode = ScheduledMode();
    if (scheduledMode != mode)
    {
        scheduledMode = mode;
        bypassesScheduledPause = false;
        alternativeOverride.reset();
    }
    interfaceMissing = !InterfaceAvailable(settings.networkInterface);
    auto adapter = AdapterName(settings.networkInterface);
    auto port = std::to_string(settings.listenPort);
    auto listen = adapter.empty() ? "0.0.0.0:" + port + ",[::]:" + port : adapter + ":" + port;
    if (interfaceMissing)
    {
        listen.clear();
    }
    auto paused = IsPaused();
    auto alternative = UsesAlternative();
    bool networkChanged = appliedInterface != listen;
    if (!configure && !networkChanged && appliedPause == paused && appliedAlternative == alternative)
    {
        return;
    }
    if (networkChanged)
    {
        session->pause();
    }
    lt::settings_pack pack;
    if (configure || networkChanged)
    {
        pack.set_bool(lt::settings_pack::enable_upnp, settings.portMapping && !interfaceMissing);
        pack.set_bool(lt::settings_pack::enable_natpmp, settings.portMapping && !interfaceMissing);
        pack.set_str(lt::settings_pack::listen_interfaces, listen);
        pack.set_str(lt::settings_pack::outgoing_interfaces, adapter);
    }
    if (configure)
    {
        pack.set_int(lt::settings_pack::active_downloads, settings.activeDownloads ? settings.activeDownloads : -1);
        pack.set_int(lt::settings_pack::active_seeds, settings.activeSeeds ? settings.activeSeeds : -1);
        pack.set_int(lt::settings_pack::active_limit, -1);
        pack.set_int(lt::settings_pack::connections_limit, settings.connections ? settings.connections : INT_MAX);
    }
    auto const& limits = alternative ? settings.alternative : settings.limits;
    pack.set_int(lt::settings_pack::download_rate_limit, limits.download);
    pack.set_int(lt::settings_pack::upload_rate_limit, limits.upload);
    session->apply_settings(pack);
    if (paused)
    {
        session->pause();
    }
    else
    {
        session->resume();
    }
    appliedInterface = std::move(listen);
    appliedPause = paused;
    appliedAlternative = alternative;
}

void Engine::State::LimitSeeds()
{
    if (settings.ratio == 0 && settings.seedingMinutes == 0)
    {
        return;
    }
    std::vector<std::string> ids;
    for (auto const& [id, torrent] : torrents)
    {
        if (Contains(limitingSeeds, id))
        {
            continue;
        }
        if (ReachedSeedLimit(torrent))
        {
            ids.push_back(id);
            if (ids.size() == targetLimit)
            {
                break;
            }
        }
    }
    if (ids.empty())
    {
        return;
    }
    limitingSeeds.insert(limitingSeeds.end(), ids.begin(), ids.end());
    Act(ids, [this, ids](Json outcome)
    {
        for (auto const& id : ids)
        {
            std::erase(limitingSeeds, id);
        }
        if (!outcome.at("ok").get<bool>())
        {
            diagnostics.Write("seeding_limit", "", "pause_failed");
        }
    }, [this](auto const& ids, Reply reply)
    {
        auto seeds = ids;
        std::erase_if(seeds, [this](auto const& id) { return !ReachedSeedLimit(torrents.at(id)); });
        if (seeds.empty())
        {
            reply(Success());
            return;
        }
        SetIntent(seeds, Intent::Paused, reply);
    });
}

bool Engine::State::ReachedSeedLimit(Torrent const& torrent) const
{
    if (!torrent.status.is_finished || torrent.facts.intent == Intent::Paused ||
        torrent.facts.ignoresSeedLimits)
    {
        return false;
    }
    auto downloaded = std::max(torrent.status.all_time_download, torrent.status.total_done);
    bool ratioReached = settings.ratio > 0 && downloaded > 0 &&
        static_cast<double>(torrent.status.all_time_upload) / downloaded >= settings.ratio;
    bool timeReached = settings.seedingMinutes > 0 &&
        torrent.status.finished_duration.count() >= static_cast<std::int64_t>(settings.seedingMinutes) * 60;
    return ratioReached || timeReached;
}
}
