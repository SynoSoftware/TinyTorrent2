#include <libtorrent/add_torrent_params.hpp>
#include <libtorrent/address.hpp>
#include <libtorrent/bencode.hpp>
#include <libtorrent/create_torrent.hpp>
#include <libtorrent/ip_filter.hpp>
#include <libtorrent/load_torrent.hpp>
#include <libtorrent/session.hpp>
#include <libtorrent/session_params.hpp>
#include <libtorrent/settings_pack.hpp>
#include <libtorrent/torrent_status.hpp>
#include <algorithm>
#include <chrono>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <string>
#include <thread>
#include <Windows.h>
#include <TlHelp32.h>

namespace lt = libtorrent;

// The checks stop the peer through its stop file, but a check whose process is
// killed never writes that file, so the peer also ends when its parent ends.
static HANDLE OpenParent()
{
    auto snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE)
    {
        return nullptr;
    }
    PROCESSENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    DWORD parent = 0;
    for (auto found = Process32FirstW(snapshot, &entry); found; found = Process32NextW(snapshot, &entry))
    {
        if (entry.th32ProcessID == GetCurrentProcessId())
        {
            parent = entry.th32ParentProcessID;
            break;
        }
    }
    CloseHandle(snapshot);
    return OpenProcess(SYNCHRONIZE, FALSE, parent);
}

int main(int argc, char** argv)
{
    try
    {
        auto parent = OpenParent();
        if (parent == nullptr)
        {
            throw std::runtime_error("The parent process has exited");
        }
        auto mode = argc == 4 ? std::string(argv[3]) : "seed";
        if (argc < 2 || argc > 4 || (mode != "seed" && mode != "download" && mode != "seed-files" && mode != "load"))
            throw std::runtime_error("Usage: Transfer evidence-directory [product-port] [seed|download|seed-files|load]");
        bool isLeecher = mode == "download";
        bool multiple = mode == "seed-files";
        bool load = mode == "load";
        auto directory = std::filesystem::absolute(argv[1]);
        auto seed = directory / "seed";
        auto destination = directory / (isLeecher ? "leecher" : "download");
        std::filesystem::create_directories(seed);
        std::filesystem::create_directories(destination);
        // Each run writes the payload twice, once here and once in the engine's
        // download, so it is as small as the checks allow. The wanted file
        // outlasts the two ten-second rate measurements of SelectedTransfer.
        // The skipped file is exactly one piece, so no piece spans both files.
        constexpr std::int64_t piece = 256 * 1024;
        std::vector<lt::create_file_entry> files = multiple ?
            std::vector<lt::create_file_entry>{{"selection/skip.bin", piece},
                {"selection/wanted.bin", 64 * piece}} :
            std::vector<lt::create_file_entry>{{"transfer.bin", 16 * piece}};
        std::int64_t total = 0;
        std::string block(65536, '\0');
        for (auto const& file : files)
        {
            auto path = seed / file.filename;
            std::filesystem::create_directories(path.parent_path());
            std::ofstream content(path, std::ios::binary | std::ios::trunc);
            content.exceptions(std::ios::badbit | std::ios::failbit);
            for (std::int64_t index = 0; index < file.size / std::int64_t(block.size()); ++index)
            {
                for (size_t offset = 0; offset < block.size(); ++offset)
                    block[offset] = static_cast<char>((offset * 37 + index * 19) & 255);
                content.write(block.data(), block.size());
            }
            total += file.size;
        }
        lt::create_torrent creator(files, piece, lt::create_torrent::v1_only);
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
            : lt::settings_pack::upload_rate_limit, load ? 64 * 1024 : 1024 * 1024);
        if (load)
            settings.set_int(lt::settings_pack::unchoke_slots_limit, 120);
        lt::session session{lt::session_params(settings)};
        auto filter = session.get_peer_class_filter();
        auto loopback = lt::make_address("127.0.0.1");
        filter.add_rule(loopback, loopback,
            1U << static_cast<std::uint32_t>(lt::session::global_peer_class_id));
        session.set_peer_class_filter(filter);
        std::vector<lt::torrent_handle> handles;
        for (int index = 0; index < (load ? 120 : 1); ++index)
        {
            auto file = torrent;
            if (load)
            {
                auto entry = creator.generate();
                entry["info"]["name"] = "load-" + std::to_string(index) + ".bin";
                std::vector<char> encoded;
                lt::bencode(std::back_inserter(encoded), entry);
                file = directory / ("load-" + std::to_string(index) + ".torrent");
                std::ofstream output(file, std::ios::binary | std::ios::trunc);
                output.exceptions(std::ios::badbit | std::ios::failbit);
                output.write(encoded.data(), encoded.size());
            }
            auto addition = lt::load_torrent_file(file.string());
            addition.save_path = (isLeecher ? destination : seed).string();
            addition.flags &= ~(lt::torrent_flags::paused | lt::torrent_flags::auto_managed);
            // Distinct torrents share one verified seed file; the product downloads real bytes.
            if (load)
                addition.renamed_files[lt::file_index_t(0)] = "transfer.bin";
            handles.push_back(session.add_torrent(addition));
        }
        auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(30);
        while (!isLeecher && std::any_of(handles.begin(), handles.end(),
            [](auto const& handle) { return !handle.status().is_seeding; }))
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
            << "\nbytes=" << total << "\nproduct_port=" << port << std::endl;
        while (!std::filesystem::exists(directory / "stop") && WaitForSingleObject(parent, 0) == WAIT_TIMEOUT)
        {
            std::int64_t uploaded = 0;
            std::int64_t downloaded = 0;
            int peers = 0;
            for (auto& handle : handles)
            {
                auto status = handle.status();
                if (status.num_connections == 0) handle.clear_peers();
                handle.connect_peer(peer);
                uploaded += status.total_payload_upload;
                downloaded += status.total_payload_download;
                peers += status.num_peers;
            }
            std::cout << "uploaded=" << uploaded << " downloaded=" << downloaded
                << " peers=" << peers << std::endl;
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
