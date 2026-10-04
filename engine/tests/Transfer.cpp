#include <libtorrent/add_torrent_params.hpp>
#include <libtorrent/address.hpp>
#include <libtorrent/create_torrent.hpp>
#include <libtorrent/ip_filter.hpp>
#include <libtorrent/load_torrent.hpp>
#include <libtorrent/session.hpp>
#include <libtorrent/session_params.hpp>
#include <libtorrent/settings_pack.hpp>
#include <libtorrent/torrent_status.hpp>
#include <chrono>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <string>
#include <thread>

namespace lt = libtorrent;

int main(int argc, char** argv)
{
    try
    {
        auto mode = argc == 4 ? std::string(argv[3]) : "seed";
        if (argc < 2 || argc > 4 || (mode != "seed" && mode != "download"))
            throw std::runtime_error("Usage: Transfer evidence-directory [product-port] [seed|download]");
        bool isLeecher = mode == "download";
        auto directory = std::filesystem::absolute(argv[1]);
        auto seed = directory / "seed";
        auto destination = directory / (isLeecher ? "leecher" : "download");
        std::filesystem::create_directories(seed);
        std::filesystem::create_directories(destination);
        auto payload = seed / "transfer.bin";
        std::ofstream content(payload, std::ios::binary | std::ios::trunc);
        content.exceptions(std::ios::badbit | std::ios::failbit);
        std::string block(65536, '\0');
        for (int index = 0; index < 1024; ++index)
        {
            for (size_t offset = 0; offset < block.size(); ++offset)
                block[offset] = static_cast<char>((offset * 37 + index * 19) & 255);
            content.write(block.data(), block.size());
        }
        content.close();

        lt::create_torrent creator({{"transfer.bin", 64 * 1024 * 1024}},
            256 * 1024, lt::create_torrent::v1_only);
        lt::set_piece_hashes(creator, seed.string());
        auto bytes = creator.generate_buf();
        auto torrent = directory / "transfer.torrent";
        std::ofstream metadata(torrent, std::ios::binary | std::ios::trunc);
        metadata.exceptions(std::ios::badbit | std::ios::failbit);
        metadata.write(bytes.data(), bytes.size());
        metadata.close();

        lt::settings_pack settings;
        settings.set_str(lt::settings_pack::listen_interfaces, "127.0.0.1:0");
        settings.set_bool(lt::settings_pack::enable_dht, false);
        settings.set_bool(lt::settings_pack::enable_lsd, false);
        settings.set_bool(lt::settings_pack::enable_upnp, false);
        settings.set_bool(lt::settings_pack::enable_natpmp, false);
        settings.set_int(isLeecher ? lt::settings_pack::download_rate_limit
            : lt::settings_pack::upload_rate_limit, 1024 * 1024);
        lt::session session{lt::session_params(settings)};
        auto filter = session.get_peer_class_filter();
        auto loopback = lt::make_address("127.0.0.1");
        filter.add_rule(loopback, loopback,
            1U << static_cast<std::uint32_t>(lt::session::global_peer_class_id));
        session.set_peer_class_filter(filter);
        auto addition = lt::load_torrent_file(torrent.string());
        addition.save_path = (isLeecher ? destination : seed).string();
        addition.flags &= ~(lt::torrent_flags::paused | lt::torrent_flags::auto_managed);
        auto handle = session.add_torrent(addition);
        auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(30);
        while (!isLeecher && !handle.status().is_seeding)
        {
            if (std::chrono::steady_clock::now() >= deadline)
                throw std::runtime_error("Seed verification did not complete");
            std::this_thread::sleep_for(std::chrono::milliseconds(100));
        }
        auto port = argc >= 3 ? std::stoi(argv[2]) : 6881;
        if (port < 1 || port > 65535) throw std::runtime_error("Invalid product port");
        lt::tcp::endpoint peer(lt::make_address("127.0.0.1"), static_cast<unsigned short>(port));
        std::cout << "ready\ntorrent=" << torrent.string()
            << "\ndestination=" << destination.string()
            << "\nbytes=67108864\nproduct_port=" << port << std::endl;
        while (!std::filesystem::exists(directory / "stop"))
        {
            auto status = handle.status();
            if (status.num_connections == 0) handle.clear_peers();
            handle.connect_peer(peer);
            std::cout << "uploaded=" << status.total_payload_upload
                << " downloaded=" << status.total_payload_download
                << " peers=" << status.num_peers << std::endl;
            std::this_thread::sleep_for(std::chrono::seconds(1));
        }
        return 0;
    }
    catch (std::exception const& error)
    {
        std::cerr << error.what() << std::endl;
        return 1;
    }
}
