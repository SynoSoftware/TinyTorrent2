#include "Engine/State.h"
#include <winsock2.h>
#include <ws2tcpip.h>
#include <iphlpapi.h>
#include <libtorrent/ip_filter.hpp>
#include <libtorrent/settings_pack.hpp>
#include <algorithm>
#include <cctype>
#include <climits>

namespace tt
{
namespace
{
// GetAdaptersAddresses documents 15 KB as a first buffer that rarely needs to
// grow. When it asks for more, the buffer grows up to adapterLimit.
constexpr ULONG adapterBuffer = 15 * 1024;
constexpr ULONG adapterLimit = 1024 * 1024;
// A schedule period starts and ends at a minute of the day.
constexpr int dayMinutes = 24 * 60;

}

std::optional<NetworkAdapter> Engine::State::FindAdapter(std::string const& name)
{
    ULONG size = adapterBuffer;
    std::vector<char> buffer(size);
    auto read = [&]
    {
        return GetAdaptersAddresses(AF_UNSPEC, GAA_FLAG_SKIP_ANYCAST |
            GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_DNS_SERVER, nullptr,
            reinterpret_cast<IP_ADAPTER_ADDRESSES*>(buffer.data()), &size);
    };
    auto outcome = read();
    if (outcome == ERROR_BUFFER_OVERFLOW && size <= adapterLimit)
    {
        buffer.resize(size);
        outcome = read();
    }
    if (outcome != NO_ERROR)
    {
        return {};
    }
    for (auto adapter = reinterpret_cast<IP_ADAPTER_ADDRESSES*>(buffer.data());
        adapter; adapter = adapter->Next)
    {
        if (_stricmp(adapter->AdapterName, name.c_str()) == 0)
        {
            if (adapter->OperStatus != IfOperStatusUp || !adapter->FirstUnicastAddress)
            {
                return {};
            }
            NetworkAdapter found{adapter->IfIndex, adapter->Ipv6IfIndex};
            for (auto address = adapter->FirstUnicastAddress; address; address = address->Next)
            {
                auto socket = address->Address.lpSockaddr;
                if (!socket || (socket->sa_family != AF_INET && socket->sa_family != AF_INET6))
                {
                    continue;
                }
                char text[INET6_ADDRSTRLEN];
                auto bytes = socket->sa_family == AF_INET ?
                    static_cast<void*>(&reinterpret_cast<sockaddr_in*>(socket)->sin_addr) :
                    static_cast<void*>(&reinterpret_cast<sockaddr_in6*>(socket)->sin6_addr);
                if (!InetNtopA(socket->sa_family, bytes, text, sizeof(text)))
                {
                    continue;
                }
                std::string value(text);
                if (socket->sa_family == AF_INET6)
                {
                    auto scope = reinterpret_cast<sockaddr_in6*>(socket)->sin6_scope_id;
                    if (scope)
                    {
                        value += "%" + std::to_string(scope);
                    }
                }
                found.addresses.push_back(std::move(value));
            }
            return found.addresses.empty() ? std::nullopt : std::optional(std::move(found));
        }
    }
    return {};
}

namespace
{
// Windows names an adapter by its GUID in upper case, and libtorrent matches
// that name exactly.
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
    auto yesterday = (day + 6) % 7;
    auto startedYesterday = std::find(days.begin(), days.end(), yesterday) != days.end();
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
            value.at(key) < 0 || value.at(key) >= dayMinutes)
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
    // Windows counts days from Sunday, and the schedule from Monday.
    auto day = (time.wDayOfWeek + 6) % 7;
    for (auto const& period : settings.schedule)
    {
        if (!period.Contains(day, time.wHour * 60 + time.wMinute))
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

std::optional<PauseReason> Engine::State::Paused() const
{
    return Paused(IsAdapterMissing());
}

std::optional<PauseReason> Engine::State::Paused(bool adapterMissing) const
{
    if (settings.allPaused || launchPaused)
        return PauseReason::Manual;
    if (scheduledMode == ScheduleMode::Paused && !bypassesScheduledPause)
        return PauseReason::Schedule;
    if (adapterMissing)
        return PauseReason::Adapter;
    if (SuspendsForConnectionTest())
        return PauseReason::ConnectionTest;
    return std::nullopt;
}

bool Engine::State::IsAdapterMissing() const
{
    return applied && applied->route.adapterMissing;
}

LimitMode Engine::State::CurrentLimits() const
{
    if (limitOverride)
    {
        return *limitOverride;
    }
    if (!settings.scheduleEnabled)
    {
        return settings.limitMode;
    }
    return scheduledMode == ScheduleMode::Alternative ? LimitMode::Alternative : LimitMode::Speed;
}

Engine::State::Policy::Policy(Settings const& settings, Environment const& environment)
{
    auto adapter = AdapterName(settings.networkAdapter);
    auto family = settings.proxy.type == ProxyType::None ? settings.ipFamily : IpFamily::Both;
    std::vector<std::string> interfaces;
    if (adapter.empty())
    {
        if (family != IpFamily::Ipv6)
            interfaces.push_back("0.0.0.0");
        if (family != IpFamily::Ipv4)
            interfaces.push_back("::");
    }
    else if (environment.adapter)
    {
        if (family == IpFamily::Both)
            interfaces.push_back(adapter);
        else
            for (auto const& address : environment.adapter->addresses)
                if ((address.find(':') != std::string::npos) == (family == IpFamily::Ipv6))
                    interfaces.push_back(address);
    }
    route = {settings.proxy, adapter, interfaces.empty()};
    auto port = std::to_string(settings.listenPort);
    std::string listen;
    std::string outgoing;
    for (auto const& address : interfaces)
    {
        if (!listen.empty())
        {
            listen += ",";
            outgoing += ",";
        }
        listen += (address.find(':') == std::string::npos ? address : "[" + address + "]") + ":" + port;
        outgoing += address;
    }
    if (adapter.empty())
    {
        outgoing.clear();
    }
    if (route.adapterMissing)
    {
        outgoing = adapter;
    }
    connection = {std::move(listen), std::move(outgoing), settings.transport, family, settings.limitsLan,
        settings.encryption};
    // Peers cannot connect in through a proxy, so there is no port to forward.
    maps = settings.mapsPorts && !route.adapterMissing && settings.proxy.type == ProxyType::None;
    // libtorrent gives each drive letter or server this many checking threads,
    // and the drive turns run one check at a time on a drive, so each running
    // check gets the threads the setting names.
    session = {settings.activeDownloads, settings.activeSeeds, settings.activeTotal,
        settings.diskBufferMib, settings.checkingMib, settings.hashingThreads,
        settings.fileLimit,
        settings.ignoresSlow, settings.slowDownload, settings.slowUpload, settings.slowWait,
        settings.outgoingRate, settings.connections, settings.dht, settings.lsd, settings.includesOverhead};
    torrents = {settings.torrentConnections, settings.pex};
    intervals = {settings.recentInterval, settings.historyInterval};
    rates = settings.Caps(environment.limits);
    paused = environment.paused || route.adapterMissing;
}

void Engine::State::RefreshPolicy()
{
    auto mode = ScheduledMode();
    if (scheduledMode != mode)
    {
        scheduledMode = mode;
        bypassesScheduledPause = false;
        limitOverride.reset();
    }
    Policy::Environment environment{.paused = Paused(false).has_value(), .limits = CurrentLimits()};
    if (auto adapter = AdapterName(settings.networkAdapter); !adapter.empty())
        environment.adapter = FindAdapter(adapter);
    Policy next(settings, environment);
    if (applied == next)
        return;
    bool initial = !applied;
    bool routeChanged = initial || applied->route != next.route;
    bool connectionChanged = initial || applied->connection != next.connection;
    bool familyChanged = initial || applied->connection.family != next.connection.family;
    bool lanChanged = initial || applied->connection.limitsLan != next.connection.limitsLan;
    bool mapsChanged = initial || applied->maps != next.maps;
    bool sessionChanged = initial || applied->session != next.session;
    bool torrentsChanged = initial || applied->torrents != next.torrents;
    bool intervalsChanged = initial || applied->intervals != next.intervals;
    bool ratesChanged = initial || applied->rates != next.rates;
    bool pauseChanged = initial || applied->paused != next.paused;
    // Connection policy and peer classes apply to new peers, so changing them
    // reconnects existing peers too.
    bool reconnects = routeChanged || connectionChanged;
    if (routeChanged)
    {
        std::lock_guard lock(checkGate);
        checkStop.request_stop();
        checkStop = std::stop_source{};
        requestedCheck.reset();
        proxyOutcome.reset();
    }
    if (initial || applied->connection.listen != next.connection.listen || applied->route.proxy != next.route.proxy)
    {
        externalIpv4.clear();
        externalIpv6.clear();
    }
    if (reconnects)
    {
        session->pause();
    }
    lt::settings_pack pack;
    if (reconnects || mapsChanged)
    {
        auto const& connection = next.connection;
        pack.set_bool(lt::settings_pack::enable_upnp, next.maps);
        pack.set_bool(lt::settings_pack::enable_natpmp, next.maps);
        pack.set_str(lt::settings_pack::listen_interfaces, connection.listen);
        pack.set_str(lt::settings_pack::outgoing_interfaces, connection.outgoing);
        pack.set_bool(lt::settings_pack::enable_incoming_tcp, connection.transport != Transport::Utp);
        pack.set_bool(lt::settings_pack::enable_outgoing_tcp, connection.transport != Transport::Utp);
        pack.set_bool(lt::settings_pack::enable_incoming_utp, connection.transport != Transport::Tcp);
        pack.set_bool(lt::settings_pack::enable_outgoing_utp, connection.transport != Transport::Tcp);
        if (familyChanged)
        {
            lt::ip_filter filter;
            if (connection.family == IpFamily::Ipv4)
            {
                filter.add_rule(lt::address_v6(), lt::make_address_v6("ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff"),
                    lt::ip_filter::blocked);
            }
            else if (connection.family == IpFamily::Ipv6)
            {
                filter.add_rule(lt::address_v4(), lt::address_v4(0xffffffff), lt::ip_filter::blocked);
            }
            session->set_ip_filter(filter);
        }
        pack.set_bool(lt::settings_pack::apply_ip_filter_to_trackers, true);
        if (lanChanged)
        {
            for (int type = 0; type < lt::peer_class_type_filter::num_socket_types; ++type)
            {
                auto socket = static_cast<lt::peer_class_type_filter::socket_type_t>(type);
                if (connection.limitsLan)
                {
                    peerClasses.add(socket, lt::session::global_peer_class_id);
                }
                else
                {
                    peerClasses.remove(socket, lt::session::global_peer_class_id);
                }
            }
            session->set_peer_class_type_filter(peerClasses);
        }
        auto encryption = connection.encryption;
        auto policy = encryption == Encryption::Required ? lt::settings_pack::pe_forced :
            encryption == Encryption::Disabled ? lt::settings_pack::pe_disabled : lt::settings_pack::pe_enabled;
        pack.set_int(lt::settings_pack::out_enc_policy, policy);
        pack.set_int(lt::settings_pack::in_enc_policy, policy);
        // Without prefer_rc4 the side that accepts the connection chooses
        // plaintext, so only the handshake is encrypted.
        pack.set_int(lt::settings_pack::allowed_enc_level,
            encryption == Encryption::Required ? lt::settings_pack::pe_rc4 : lt::settings_pack::pe_both);
        pack.set_bool(lt::settings_pack::prefer_rc4,
            encryption == Encryption::Preferred || encryption == Encryption::Required);
        auto const& proxy = next.route.proxy;
        auto signsIn = !proxy.username.empty();
        auto type = proxy.type == ProxyType::Socks5 ? (signsIn ? lt::settings_pack::socks5_pw : lt::settings_pack::socks5) :
            proxy.type == ProxyType::Socks4 ? lt::settings_pack::socks4 :
            proxy.type == ProxyType::Http ? (signsIn ? lt::settings_pack::http_pw : lt::settings_pack::http) :
            lt::settings_pack::none;
        pack.set_int(lt::settings_pack::proxy_type, type);
        pack.set_str(lt::settings_pack::proxy_hostname, proxy.host);
        pack.set_int(lt::settings_pack::proxy_port, proxy.port);
        pack.set_str(lt::settings_pack::proxy_username, proxy.username);
        pack.set_str(lt::settings_pack::proxy_password, proxy.password);
    }
    if (sessionChanged)
    {
        auto const& chosen = next.session;
        pack.set_int(lt::settings_pack::active_downloads, chosen.activeDownloads ? chosen.activeDownloads : -1);
        pack.set_int(lt::settings_pack::max_queued_disk_bytes, chosen.diskBufferMib * 1024 * 1024);
        // libtorrent counts checking memory in 16 KiB blocks.
        pack.set_int(lt::settings_pack::checking_mem_usage, chosen.checkingMib * 64);
        pack.set_int(lt::settings_pack::hashing_threads, chosen.hashingThreads);
        pack.set_int(lt::settings_pack::file_pool_size, chosen.fileLimit);
        pack.set_int(lt::settings_pack::active_seeds, chosen.activeSeeds ? chosen.activeSeeds : -1);
        pack.set_int(lt::settings_pack::active_limit, chosen.activeTotal ? chosen.activeTotal : -1);
        pack.set_bool(lt::settings_pack::dont_count_slow_torrents, chosen.ignoresSlow);
        pack.set_int(lt::settings_pack::inactive_down_rate, chosen.slowDownload);
        pack.set_int(lt::settings_pack::inactive_up_rate, chosen.slowUpload);
        pack.set_int(lt::settings_pack::auto_manage_startup, chosen.slowWait);
        // libtorrent starts every check and the drive pass decides which wait.
        // A limit of 0 or less would pause every check.
        pack.set_int(lt::settings_pack::active_checking, INT_MAX);
        pack.set_int(lt::settings_pack::connection_speed, chosen.outgoingRate);
        pack.set_bool(lt::settings_pack::enable_dht, chosen.dht);
        pack.set_bool(lt::settings_pack::enable_lsd, chosen.lsd);
        pack.set_bool(lt::settings_pack::rate_limit_ip_overhead, chosen.includesOverhead);
        pack.set_int(lt::settings_pack::active_dht_limit, -1);
        pack.set_int(lt::settings_pack::active_lsd_limit, -1);
        pack.set_int(lt::settings_pack::active_tracker_limit, -1);
        pack.set_int(lt::settings_pack::connections_limit, chosen.connections ? chosen.connections : INT_MAX);
    }
    if (intervalsChanged)
    {
        history.Configure(next.intervals.recent, next.intervals.history);
        for (auto& [id, torrent] : torrents)
        {
            torrent.history.Configure(next.intervals.recent, next.intervals.history);
        }
    }
    if (torrentsChanged)
    {
        for (auto& [id, torrent] : torrents)
        {
            ApplyPolicy(torrent.handle);
        }
        for (auto const& [id, preview] : previews)
        {
            ApplyPolicy(preview.handle);
        }
        for (auto const& [id, addition] : additions)
        {
            ApplyPolicy(addition.handle);
        }
    }
    if (ratesChanged)
    {
        pack.set_int(lt::settings_pack::download_rate_limit, next.rates.download);
        pack.set_int(lt::settings_pack::upload_rate_limit, next.rates.upload);
    }
    if (reconnects || mapsChanged || sessionChanged || ratesChanged)
        session->apply_settings(pack);
    if (reconnects || pauseChanged)
    {
        if (next.paused)
            session->pause();
        else
            session->resume();
    }
    applied = std::move(next);
    // libtorrent cannot tell a proxy that fails from peers that are offline,
    // so the engine checks the proxy itself.
    if (routeChanged && settings.proxy.type != ProxyType::None)
    {
        CheckProxy(settings.proxy, [this, checked = settings.proxy](std::optional<ProxyCheck> check)
        {
            if (check && settings.proxy == checked)
            {
                proxyOutcome = check->outcome;
            }
        });
    }
}

void Engine::State::ApplyPolicy(lt::torrent_handle const& handle)
{
    if (!handle.is_valid())
    {
        return;
    }
    handle.set_max_connections(settings.torrentConnections ? settings.torrentConnections : -1);
    if (settings.pex)
    {
        handle.unset_flags(lt::torrent_flags::disable_pex);
    }
    else
    {
        handle.set_flags(lt::torrent_flags::disable_pex);
    }
}

void Engine::State::LimitSeeds()
{
    if (settings.ratio == 0 && settings.seedingMinutes == 0 && settings.inactiveMinutes == 0)
    {
        return;
    }
    if ((settings.seedingMinutes > 0 || settings.inactiveMinutes > 0) &&
        seedQueue.empty() && seedQueries.empty())
    {
        // Idle torrents do not emit state updates as their elapsed durations increase.
        for (auto const& [id, torrent] : torrents)
        {
            if (!torrent.waiting && torrent.status.is_finished &&
                torrent.facts.intent != Intent::Paused && !torrent.facts.ignoresSeedLimits)
            {
                seedQueue.push_back(torrent.handle);
            }
        }
    }
    QuerySeeds();
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
            if (ids.size() == torrentLimit)
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
    Act(ids, [this, ids](Outcome outcome)
    {
        for (auto const& id : ids)
        {
            std::erase(limitingSeeds, id);
        }
        if (outcome.error)
        {
            log.Write("seeding_limit", "", "pause_failed");
        }
    }, [this](auto const& ids, auto done)
    {
        auto seeds = ids;
        std::erase_if(seeds, [this](auto const& id) { return !ReachedSeedLimit(torrents.at(id)); });
        if (seeds.empty())
        {
            done({});
            return;
        }
        SetIntent(seeds, Intent::Paused, done, Requester::Engine);
    });
}

void Engine::State::QuerySeeds()
{
    if (IsShuttingDown() || (settings.seedingMinutes == 0 && settings.inactiveMinutes == 0))
    {
        seedQueue.clear();
        return;
    }
    // Each query produces a separate alert; a large library must not fill
    // libtorrent's alert queue before the engine can drain it.
    constexpr std::size_t pendingLimit = 64;
    while (!seedQueue.empty() && seedQueries.size() < pendingLimit)
    {
        auto handle = seedQueue.back();
        seedQueue.pop_back();
        auto torrent = Find(handle);
        if (!torrent || !torrent->status.is_finished ||
            torrent->facts.intent == Intent::Paused || torrent->facts.ignoresSeedLimits)
        {
            continue;
        }
        seedQueries.insert(handle);
        handle.post_status(seedQuery);
    }
}

bool Engine::State::ReachedSeedLimit(Torrent const& torrent) const
{
    bool blockedMove = torrent.completion.IsSettling() &&
        !torrent.facts.finalFolder.empty() && !torrent.facts.marker &&
        !torrent.FilesBusy() && torrent.MoveBlocked();
    if ((settings.ratio == 0 && settings.seedingMinutes == 0 && settings.inactiveMinutes == 0) ||
        !torrent.status.is_finished || (!torrent.completion.IsIdle() && !blockedMove) ||
        torrent.facts.intent == Intent::Paused ||
        torrent.facts.ignoresSeedLimits)
    {
        return false;
    }
    auto downloaded = std::max(torrent.status.all_time_download, torrent.status.total_done);
    bool ratioReached = settings.ratio > 0 && downloaded > 0 &&
        static_cast<double>(torrent.status.all_time_upload) / downloaded >= settings.ratio;
    bool timeReached = settings.seedingMinutes > 0 &&
        torrent.status.finished_duration.count() >= static_cast<std::int64_t>(settings.seedingMinutes) * 60;
    auto inactiveFor = std::chrono::minutes(settings.inactiveMinutes);
    bool inactivityReached = settings.inactiveMinutes > 0 && torrent.status.finished_duration >= inactiveFor &&
        (torrent.status.last_upload == lt::time_point{} || lt::clock_type::now() - torrent.status.last_upload >= inactiveFor);
    if (settings.seedRule == SeedRule::All)
        return (settings.ratio == 0 || ratioReached) &&
            (settings.seedingMinutes == 0 || timeReached) &&
            (settings.inactiveMinutes == 0 || inactivityReached);
    return ratioReached || timeReached || inactivityReached;
}
}
