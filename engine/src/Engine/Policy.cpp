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

std::vector<std::string> Interfaces(std::string const& name, IpFamily family)
{
    if (name.empty())
    {
        if (family == IpFamily::Ipv4)
        {
            return {"0.0.0.0"};
        }
        if (family == IpFamily::Ipv6)
        {
            return {"::"};
        }
        return {"0.0.0.0", "::"};
    }
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
            if (family == IpFamily::Both)
            {
                return {name};
            }
            std::vector<std::string> addresses;
            for (auto address = adapter->FirstUnicastAddress; address; address = address->Next)
            {
                auto socket = address->Address.lpSockaddr;
                if (!socket || (family == IpFamily::Ipv4 ? socket->sa_family != AF_INET :
                    socket->sa_family != AF_INET6))
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
                addresses.push_back(std::move(value));
            }
            return addresses;
        }
    }
    return {};
}

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

bool Engine::State::IsPaused() const
{
    return IsPausedByChoice() || adapterMissing || SuspendsForConnectionTest();
}

bool Engine::State::IsPausedByChoice() const
{
    return settings.allPaused || launchPaused || (scheduledMode == ScheduleMode::Paused && !bypassesScheduledPause);
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

void Engine::State::RefreshPolicy(bool configure)
{
    auto mode = ScheduledMode();
    if (scheduledMode != mode)
    {
        scheduledMode = mode;
        bypassesScheduledPause = false;
        limitOverride.reset();
    }
    auto adapter = AdapterName(settings.networkAdapter);
    auto family = settings.proxy.type == ProxyType::None ? settings.ipFamily : IpFamily::Both;
    auto interfaces = Interfaces(adapter, family);
    adapterMissing = interfaces.empty();
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
    if (adapterMissing)
    {
        outgoing = adapter;
    }
    auto paused = IsPaused();
    auto limits = CurrentLimits();
    bool proxyChanged = appliedProxy != settings.proxy;
    // Connection policy and peer classes apply to new peers, so changing them
    // reconnects existing peers too.
    bool networkChanged = appliedListen != listen || proxyChanged || appliedEncryption != settings.encryption ||
        appliedTransport != settings.transport || appliedFamily != family || appliedLan != settings.limitsLan;
    if (!configure && !networkChanged && appliedPause == paused && appliedLimits == limits)
    {
        return;
    }
    if (appliedListen != listen || proxyChanged)
    {
        externalIpv4.clear();
        externalIpv6.clear();
    }
    if (networkChanged)
    {
        session->pause();
    }
    if (proxyChanged)
    {
        proxyOutcome.reset();
    }
    lt::settings_pack pack;
    if (configure || networkChanged)
    {
        auto const& proxy = settings.proxy;
        // Peers cannot connect in through a proxy, so there is no port to
        // forward.
        auto maps = settings.mapsPorts && !adapterMissing && proxy.type == ProxyType::None;
        pack.set_bool(lt::settings_pack::enable_upnp, maps);
        pack.set_bool(lt::settings_pack::enable_natpmp, maps);
        pack.set_str(lt::settings_pack::listen_interfaces, listen);
        pack.set_str(lt::settings_pack::outgoing_interfaces, outgoing);
        pack.set_bool(lt::settings_pack::enable_incoming_tcp, settings.transport != Transport::Utp);
        pack.set_bool(lt::settings_pack::enable_outgoing_tcp, settings.transport != Transport::Utp);
        pack.set_bool(lt::settings_pack::enable_incoming_utp, settings.transport != Transport::Tcp);
        pack.set_bool(lt::settings_pack::enable_outgoing_utp, settings.transport != Transport::Tcp);
        if (appliedFamily != family)
        {
            lt::ip_filter filter;
            if (family == IpFamily::Ipv4)
            {
                filter.add_rule(lt::address_v6(), lt::make_address_v6("ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff"),
                    lt::ip_filter::blocked);
            }
            else if (family == IpFamily::Ipv6)
            {
                filter.add_rule(lt::address_v4(), lt::address_v4(0xffffffff), lt::ip_filter::blocked);
            }
            session->set_ip_filter(filter);
        }
        pack.set_bool(lt::settings_pack::apply_ip_filter_to_trackers, true);
        if (appliedLan != settings.limitsLan)
        {
            auto classes = session->get_peer_class_type_filter();
            for (int type = 0; type < lt::peer_class_type_filter::num_socket_types; ++type)
            {
                auto socket = static_cast<lt::peer_class_type_filter::socket_type_t>(type);
                if (settings.limitsLan)
                {
                    classes.add(socket, lt::session::global_peer_class_id);
                }
                else
                {
                    classes.remove(socket, lt::session::global_peer_class_id);
                }
            }
            session->set_peer_class_type_filter(classes);
        }
        auto encryption = settings.encryption;
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
    if (configure)
    {
        pack.set_int(lt::settings_pack::active_downloads, settings.activeDownloads ? settings.activeDownloads : -1);
        pack.set_int(lt::settings_pack::max_queued_disk_bytes, settings.diskBufferMib * 1024 * 1024);
        // libtorrent counts checking memory in 16 KiB blocks.
        pack.set_int(lt::settings_pack::checking_mem_usage, settings.checkingMib * 64);
        pack.set_int(lt::settings_pack::hashing_threads, settings.hashingThreads);
        pack.set_int(lt::settings_pack::file_pool_size, settings.fileLimit);
        pack.set_int(lt::settings_pack::active_seeds, settings.activeSeeds ? settings.activeSeeds : -1);
        pack.set_int(lt::settings_pack::active_limit, settings.activeTotal ? settings.activeTotal : -1);
        pack.set_bool(lt::settings_pack::dont_count_slow_torrents, settings.ignoresSlow);
        pack.set_int(lt::settings_pack::inactive_down_rate, settings.slowDownload);
        pack.set_int(lt::settings_pack::inactive_up_rate, settings.slowUpload);
        pack.set_int(lt::settings_pack::auto_manage_startup, settings.slowWait);
        pack.set_int(lt::settings_pack::active_checking, settings.activeChecking);
        pack.set_int(lt::settings_pack::connection_speed, settings.outgoingRate);
        pack.set_bool(lt::settings_pack::enable_dht, settings.dht);
        pack.set_bool(lt::settings_pack::enable_lsd, settings.lsd);
        pack.set_bool(lt::settings_pack::rate_limit_ip_overhead, settings.includesOverhead);
        pack.set_int(lt::settings_pack::active_dht_limit, -1);
        pack.set_int(lt::settings_pack::active_lsd_limit, -1);
        pack.set_int(lt::settings_pack::active_tracker_limit, -1);
        pack.set_int(lt::settings_pack::connections_limit, settings.connections ? settings.connections : INT_MAX);
        history.Configure(settings.recentInterval, settings.historyInterval);
        for (auto const& [id, torrent] : torrents)
        {
            if (!torrent.deleted)
            {
                ApplyPolicy(torrent.handle);
            }
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
    auto caps = settings.Caps(limits);
    pack.set_int(lt::settings_pack::download_rate_limit, caps.download);
    pack.set_int(lt::settings_pack::upload_rate_limit, caps.upload);
    session->apply_settings(pack);
    if (paused)
    {
        session->pause();
    }
    else
    {
        session->resume();
    }
    appliedListen = std::move(listen);
    appliedProxy = settings.proxy;
    appliedEncryption = settings.encryption;
    appliedTransport = settings.transport;
    appliedFamily = family;
    appliedLan = settings.limitsLan;
    appliedPause = paused;
    appliedLimits = limits;
    // libtorrent cannot tell a proxy that fails from peers that are offline,
    // so the engine checks the proxy itself.
    if (proxyChanged && settings.proxy.type != ProxyType::None)
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
    if (settings.seedingMinutes > 0 || settings.inactiveMinutes > 0)
    {
        // Idle torrents do not emit state updates as their elapsed durations increase.
        std::vector<lt::torrent_status> seeds;
        for (auto const& [id, torrent] : torrents)
            if (!torrent.deleted && !torrent.restore && torrent.status.is_finished &&
                torrent.facts.intent != Intent::Paused && !torrent.facts.ignoresSeedLimits)
                seeds.push_back(torrent.status);
        if (!seeds.empty())
        {
            session->refresh_torrent_status(&seeds, lt::torrent_handle::query_name);
            for (auto& status : seeds)
                if (auto torrent = Find(status.handle))
                    torrent->Update(std::move(status));
        }
    }
    std::vector<std::string> ids;
    for (auto const& [id, torrent] : torrents)
    {
        if (torrent.deleted || Contains(limitingSeeds, id))
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
    Act(ids, [this, ids](Json outcome)
    {
        for (auto const& id : ids)
        {
            std::erase(limitingSeeds, id);
        }
        if (!outcome.at("ok").get<bool>())
        {
            log.Write("seeding_limit", "", "pause_failed");
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
    if ((settings.ratio == 0 && settings.seedingMinutes == 0 && settings.inactiveMinutes == 0) ||
        !torrent.status.is_finished || torrent.completionPhase == CompletionPhase::Checking ||
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
