#include <libtorrent/session.hpp>
#include <libtorrent/session_params.hpp>
#include <libtorrent/settings_pack.hpp>
#include <libtorrent/version.hpp>

#include <cstdio>

int main()
{
    // Loopback only, with discovery and port mapping off: this run proves the build without
    // touching the network or raising a Windows Firewall prompt.
    lt::settings_pack settings;
    settings.set_str(lt::settings_pack::listen_interfaces, "127.0.0.1:0");
    settings.set_bool(lt::settings_pack::enable_dht, false);
    settings.set_bool(lt::settings_pack::enable_lsd, false);
    settings.set_bool(lt::settings_pack::enable_upnp, false);
    settings.set_bool(lt::settings_pack::enable_natpmp, false);

    lt::session session{lt::session_params{settings}};
    std::printf("libtorrent %s\n", lt::version());
    return session.is_valid() ? 0 : 1;
}
