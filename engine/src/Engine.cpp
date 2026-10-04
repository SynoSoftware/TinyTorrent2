#include "Engine.h"
#include "Enums.h"
#include "Store.h"

#include <Windows.h>
#include <ShlObj.h>
#include <objbase.h>
#include <libtorrent/add_torrent_params.hpp>
#include <libtorrent/alert_types.hpp>
#include <libtorrent/magnet_uri.hpp>
#include <libtorrent/hex.hpp>
#include <libtorrent/load_torrent.hpp>
#include <libtorrent/read_resume_data.hpp>
#include <libtorrent/session.hpp>
#include <libtorrent/session_params.hpp>
#include <libtorrent/settings_pack.hpp>
#include <libtorrent/torrent_info.hpp>
#include <libtorrent/write_resume_data.hpp>
#include <chrono>
#include <algorithm>
#include <deque>
#include <fstream>
#include <map>
#include <sstream>

namespace tiny
{
std::string Utf8(std::wstring const& value)
{
    if (value.empty()) return {};
    int size = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(),
        static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (!size) throw std::runtime_error("Invalid Unicode");
    std::string text(size, '\0');
    WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(),
        static_cast<int>(value.size()), text.data(), size, nullptr, nullptr);
    return text;
}

std::wstring Wide(std::string const& value)
{
    if (value.empty()) return {};
    int size = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(),
        static_cast<int>(value.size()), nullptr, 0);
    if (!size) throw std::runtime_error("Invalid UTF-8");
    std::wstring text(size, L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(),
        static_cast<int>(value.size()), text.data(), size);
    return text;
}

static std::string Identity()
{
    GUID guid;
    if (FAILED(CoCreateGuid(&guid))) throw std::runtime_error("Cannot create identity");
    wchar_t text[40];
    StringFromGUID2(guid, text, 40);
    return Utf8(text);
}

static Json Failure(std::string code, std::string detail = {})
{
    return {{"ok", false}, {"error", {{"code", std::move(code)}, {"detail", std::move(detail)}}}};
}

static Json Success(Json data = Json::object())
{
    return {{"ok", true}, {"data", std::move(data)}};
}

class Engine::State
{
public:
    struct Torrent
    {
        std::string identity;
        lt::torrent_handle handle;
        Json facts;
        bool checkpoint = false;
        bool unsaved = true;
        std::string error;
        std::string detail;
        std::string diskError;
        std::chrono::steady_clock::time_point retryAt{};
    };

    struct Preview
    {
        std::string identity;
        std::string connection;
        lt::add_torrent_params params;
        lt::torrent_handle handle;
        std::string error;
        bool cancelled = false;
    };

    struct Addition
    {
        std::string identity;
        lt::add_torrent_params params;
        Json facts;
        std::function<void(Json)> reply;
        lt::torrent_handle handle;
        AdditionPhase phase = AdditionPhase::Adding;
    };

    std::filesystem::path directory;
    Store store;
    std::function<void()> wake;
    std::unique_ptr<lt::session> session;
    std::string identity = Identity();
    Json settings;
    std::vector<std::string> queueOrder;
    std::string language;
    std::map<std::string, Torrent> torrents;
    std::map<std::string, Preview> previews;
    std::map<std::string, Addition> additions;
    std::vector<std::shared_ptr<Preview>> parsing;
    std::deque<std::function<void()>> changes;
    std::deque<std::string> diagnostics;
    bool logging = false;
    bool committing = false;
    bool loading = true;
    bool stopping = false;
    bool pauseRequested = false;
    std::vector<lt::torrent_handle> pausing;
    std::chrono::steady_clock::time_point pauseAt{};
    bool finalRequested = false;
    bool storageFailed = false;
    std::string startupError;
    std::function<void(bool)> shutdown;
    std::chrono::steady_clock::time_point checkpointAt = std::chrono::steady_clock::now();

    State(std::filesystem::path path, std::function<void()> notification) :
        directory(std::move(path)), store(notification), wake(std::move(notification))
    {
        PWSTR downloads = nullptr;
        std::string destination;
        if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_Downloads, 0, nullptr, &downloads)))
        {
            destination = Utf8(downloads);
            CoTaskMemFree(downloads);
        }
        settings = {{"default_destination", destination},
            {"language", PRIMARYLANGID(GetUserDefaultUILanguage()) == LANG_SPANISH ? "es" : "en"}, {"theme", "system"},
            {"port_mapping", true}, {"listen_port", 6881}, {"show_add", true},
            {"all_paused", false}, {"download_limit", 0}, {"upload_limit", 0},
            {"alternative_download_limit", 10 * 1024}, {"alternative_upload_limit", 10 * 1024},
            {"alternative_limits", false}};
        language = settings.at("language").get<std::string>();

        auto loaded = std::make_shared<Json>();
        auto resumes = std::make_shared<std::map<std::string, lt::add_torrent_params>>();
        store.Run([this, loaded, resumes]
        {
            std::filesystem::create_directories(directory);
            auto file = directory / L"settings.json";
            if (!std::filesystem::exists(file)) return;
            *loaded = Json::parse(Store::Read(file));
            if (loaded->at("format") != 1) throw std::runtime_error("Unsupported store format");
            for (auto const& facts : loaded->at("torrents"))
            {
                auto id = facts.at("torrent_id").get<std::string>();
                auto bytes = Store::Read(directory / Wide(id + ".resume"));
                (*resumes)[id] = lt::read_resume_data(lt::span<char const>(bytes.data(), bytes.size()));
            }
        }, [this, loaded, resumes](StorageOutcome outcome)
        {
            if (!outcome.saved)
            {
                storageFailed = true;
                startupError = outcome.detail;
                loading = false;
                Log("startup", "", "storage_failed");
                return;
            }
            if (!loaded->is_null()) settings.update(loaded->at("settings"));
            language = settings.at("language").get<std::string>();
            lt::settings_pack pack;
            pack.set_int(lt::settings_pack::alert_mask,
                static_cast<int>(static_cast<std::uint32_t>(lt::alert_category::error |
                    lt::alert_category::storage | lt::alert_category::status)));
            session = std::make_unique<lt::session>(lt::session_params(pack));
            if (wake) session->set_alert_notify(wake);
            auto classes = session->get_peer_class_type_filter();
            for (int type = 0; type < lt::peer_class_type_filter::num_socket_types; ++type)
                classes.add(static_cast<lt::peer_class_type_filter::socket_type_t>(type), lt::session::global_peer_class_id);
            session->set_peer_class_type_filter(classes);
            ApplySettings();
            if (!loaded->is_null())
                for (auto const& facts : loaded->at("torrents"))
                {
                    auto id = facts.at("torrent_id").get<std::string>();
                    auto params = std::move(resumes->at(id));
                    params.save_path = facts.at("save_path").get<std::string>();
                    params.flags &= ~lt::torrent_flags::auto_managed;
                    params.flags |= lt::torrent_flags::paused;
                    auto handle = session->add_torrent(params);
                    torrents.emplace(id, Torrent{id, handle, facts});
                    if (facts.contains("trackers"))
                    {
                        std::vector<lt::announce_entry> trackers;
                        for (auto const& url : facts.at("trackers")) trackers.emplace_back(url.get<std::string>());
                        handle.replace_trackers(trackers);
                    }
                }
            if (!loaded->is_null()) queueOrder = loaded->value("queue_order", std::vector<std::string>{});
            ApplyQueue();
            for (auto& [id, torrent] : torrents) ApplyIntent(torrent);
            loading = false;
            Log("startup", "", "ready");
        });
    }

    Json Membership() const
    {
        Json list = Json::array();
        for (auto const& [id, torrent] : torrents) list.push_back(torrent.facts);
        return {{"format", 1}, {"settings", settings}, {"torrents", std::move(list)}, {"queue_order", queueOrder}};
    }

    void Log(std::string kind, std::string id, std::string code)
    {
        auto line = kind + " " + id + " " + code + "\n";
        if (!diagnostics.empty() && diagnostics.back() == line) return;
        if (diagnostics.size() == 32) diagnostics.pop_front();
        diagnostics.push_back(std::move(line));
    }

    void FlushLog()
    {
        if (logging || diagnostics.empty()) return;
        std::string lines;
        for (auto const& line : diagnostics) lines += line;
        diagnostics.clear();
        logging = true;
        store.Run([path = directory, lines = std::move(lines)]
        {
            auto current = path / L"engine.log";
            std::error_code error;
            if (std::filesystem::file_size(current, error) + lines.size() > 1024 * 1024 && !error)
            {
                std::filesystem::remove(path / L"engine.previous.log", error);
                std::filesystem::rename(current, path / L"engine.previous.log", error);
                if (error) return;
            }
            std::ofstream stream(current, std::ios::binary | std::ios::app);
            stream << lines;
        }, [this](StorageOutcome) { logging = false; });
    }

    void Change(std::function<void()> work)
    {
        if (changes.size() >= 64) throw std::runtime_error("commands_overloaded");
        changes.push_back(std::move(work));
        Next();
    }

    void Next()
    {
        if (committing || changes.empty()) return;
        committing = true;
        auto work = std::move(changes.front());
        changes.pop_front();
        work();
    }

    void Commit(Json document, std::function<void(StorageOutcome)> completion)
    {
        store.Write(directory / L"settings.json", [document = std::move(document)] { return document.dump(); },
            [this, completion = std::move(completion)](StorageOutcome outcome)
        {
            if (!outcome.saved) Log("membership", "", "storage_failed");
            completion(std::move(outcome));
            committing = false;
            Next();
        });
    }

    void ApplySettings()
    {
        lt::settings_pack pack;
        pack.set_bool(lt::settings_pack::enable_upnp, settings.at("port_mapping"));
        pack.set_bool(lt::settings_pack::enable_natpmp, settings.at("port_mapping"));
        auto port = std::to_string(settings.at("listen_port").get<int>());
        pack.set_str(lt::settings_pack::listen_interfaces, "0.0.0.0:" + port + ",[::]:" + port);
        bool alternative = settings.at("alternative_limits");
        pack.set_int(lt::settings_pack::download_rate_limit,
            settings.at(alternative ? "alternative_download_limit" : "download_limit"));
        pack.set_int(lt::settings_pack::upload_rate_limit,
            settings.at(alternative ? "alternative_upload_limit" : "upload_limit"));
        session->apply_settings(pack);
        if (settings.at("all_paused")) session->pause();
        else session->resume();
    }

    void ApplyIntent(Torrent& torrent)
    {
        auto metadata = torrent.handle.torrent_file();
        if (torrent.facts.contains("priorities"))
        {
            std::vector<lt::download_priority_t> priorities;
            for (auto const& priority : torrent.facts.at("priorities"))
                priorities.emplace_back(priority.get<std::uint8_t>());
            if (priorities.empty() && metadata)
                for (auto index : metadata->layout().file_range())
                    priorities.push_back(metadata->layout().pad_file_at(index) ? lt::dont_download : lt::default_priority);
            torrent.handle.prioritize_files(priorities);
        }
        bool paused = torrent.facts.at("paused");
        if (paused)
        {
            torrent.handle.unset_flags(lt::torrent_flags::auto_managed);
            torrent.handle.pause();
        }
        else
        {
            if (!metadata || torrent.facts.value("forced", false)) torrent.handle.unset_flags(lt::torrent_flags::auto_managed);
            else torrent.handle.set_flags(lt::torrent_flags::auto_managed);
            torrent.handle.resume();
        }
        torrent.unsaved = true;
    }

    std::vector<std::string> CurrentQueue() const
    {
        std::vector<std::pair<int, std::string>> positions;
        for (auto const& [id, torrent] : torrents)
        {
            auto position = static_cast<int>(torrent.handle.queue_position());
            if (position >= 0) positions.emplace_back(position, id);
        }
        std::stable_sort(positions.begin(), positions.end());
        std::vector<std::string> order;
        for (auto const& [position, id] : positions) order.push_back(id);
        return order;
    }

    void ApplyQueue()
    {
        std::erase_if(queueOrder, [this](auto const& id) { return !torrents.contains(id); });
        for (auto const& id : CurrentQueue())
            if (std::find(queueOrder.begin(), queueOrder.end(), id) == queueOrder.end()) queueOrder.push_back(id);
        int position = 0;
        for (auto const& id : queueOrder)
            if (static_cast<int>(torrents.at(id).handle.queue_position()) >= 0)
                torrents.at(id).handle.queue_position_set(lt::queue_position_t(position++));
    }

    std::string Duplicate(lt::info_hash_t const& hashes, std::string const& excluded = {}) const
    {
        auto values = Hashes(hashes);
        for (auto const& [id, torrent] : torrents)
        {
            if (id == excluded) continue;
            auto existing = ContentHashes(torrent);
            for (auto const& hash : values)
                if (std::find(existing.begin(), existing.end(), hash) != existing.end()) return id;
        }
        return {};
    }

    static Json Hashes(lt::info_hash_t const& hashes)
    {
        Json values = Json::array();
        if (hashes.has_v1()) values.push_back(lt::aux::to_hex(hashes.v1.to_string()));
        if (hashes.has_v2()) values.push_back(lt::aux::to_hex(hashes.v2.to_string()));
        return values;
    }

    static Json ContentHashes(Torrent const& torrent)
    {
        auto hashes = Hashes(torrent.handle.info_hashes());
        for (auto const& hash : torrent.facts.value("hashes", Json::array()))
            if (std::find(hashes.begin(), hashes.end(), hash) == hashes.end()) hashes.push_back(hash);
        return hashes;
    }

    static void Guard(lt::add_torrent_params& params)
    {
        params.flags &= ~(lt::torrent_flags::auto_managed | lt::torrent_flags::share_mode |
            lt::torrent_flags::seed_mode);
        params.flags |= lt::torrent_flags::default_dont_download | lt::torrent_flags::duplicate_is_error;
        params.piece_priorities.clear();
        params.file_priorities.assign(params.ti ? params.ti->num_files() : 0, lt::dont_download);
    }

    static Json Files(std::shared_ptr<lt::torrent_info const> const& metadata)
    {
        Json files = Json::array();
        if (!metadata) return files;
        auto const& storage = metadata->layout();
        for (auto index : storage.file_range())
            files.push_back({{"index", static_cast<int>(index)}, {"path", storage.file_path(index)},
                {"size", storage.file_size(index)}, {"padding", storage.pad_file_at(index)},
                {"priority", storage.pad_file_at(index) ? 0 : 4}});
        return files;
    }

    static std::filesystem::path FullPath(std::filesystem::path const& path)
    {
        return std::filesystem::absolute(path).lexically_normal();
    }

    Json SharedFiles(std::shared_ptr<lt::torrent_info const> const& metadata, std::string const& destination) const
    {
        Json names = Json::array();
        if (!metadata || destination.empty()) return names;
        std::vector<std::filesystem::path> paths;
        for (auto index : metadata->layout().file_range())
            if (!metadata->layout().pad_file_at(index))
                paths.push_back(FullPath(std::filesystem::path(Wide(destination)) / Wide(metadata->layout().file_path(index))));
        for (auto const& [id, torrent] : torrents)
        {
            auto other = torrent.handle.torrent_file();
            if (!other) continue;
            bool shared = false;
            for (auto index : other->layout().file_range())
            {
                if (other->layout().pad_file_at(index)) continue;
                auto path = FullPath(std::filesystem::path(Wide(torrent.facts.at("save_path"))) / Wide(other->layout().file_path(index)));
                shared |= std::any_of(paths.begin(), paths.end(), [&path](auto const& candidate)
                    { return CompareStringOrdinal(path.c_str(), -1, candidate.c_str(), -1, TRUE) == CSTR_EQUAL; });
                if (shared) break;
            }
            if (shared) names.push_back(other->name());
        }
        return names;
    }

    void UpdatePreview(Preview& preview)
    {
        if (!preview.handle.is_valid()) return;
        if (auto metadata = preview.handle.torrent_file())
        {
            preview.params.ti = metadata;
            preview.params.info_hashes = metadata->info_hashes();
        }
    }

    Json Describe(Preview const& preview, std::string const& destination = {}) const
    {
        auto const& params = preview.params;
        auto files = Files(params.ti);
        std::int64_t size = 0;
        std::string name = params.name;
        if (params.ti)
        {
            name = params.ti->name();
            size = params.ti->total_size();
        }
        auto hashes = params.ti ? params.ti->info_hashes() : params.info_hashes;
        auto const& urls = params.trackers;
        auto duplicate = Duplicate(hashes);
        bool merge = false;
        if (!duplicate.empty())
        {
            auto existing = torrents.at(duplicate).handle.trackers();
            merge = std::any_of(urls.begin(), urls.end(), [&existing](auto const& url)
                { return std::none_of(existing.begin(), existing.end(), [&url](auto const& tracker) { return tracker.url == url; }); });
        }
        if (name.empty()) name = Hashes(hashes).front().get<std::string>();
        return {{"preview_id", preview.identity}, {"name", name}, {"size", size},
            {"files", std::move(files)}, {"duplicate", duplicate}, {"hashes", Hashes(hashes)},
            {"trackers", urls}, {"merge_available", merge}, {"metadata_ready", bool(params.ti)},
            {"error", preview.error}, {"shared_with", SharedFiles(params.ti, destination)}};
    }

    void Execute(Json const& request, std::function<void(Json)> reply)
    {
        auto command = request.at("command").get<std::string>();
        if (command == "snapshot") { reply(Success(Snapshot())); return; }
        if (stopping) { reply(Failure("stopping")); return; }
        if (loading || !session) { reply(Failure(storageFailed ? "storage_failed" : "starting")); return; }
        if (command == "settings")
        {
            auto choices = request.at("changes");
            if (!choices.is_object() || choices.empty()) { reply(Failure("invalid_request")); return; }
            for (auto const& [key, value] : choices.items())
            {
                bool supported = (key == "language" && (value == "en" || value == "es")) ||
                    (key == "theme" && (value == "system" || value == "light" || value == "dark")) ||
                    ((key == "show_add" || key == "alternative_limits") && value.is_boolean()) ||
                    ((key == "download_limit" || key == "upload_limit" || key == "alternative_download_limit" ||
                        key == "alternative_upload_limit") && value.is_number_integer() && value >= 0 && value <= INT_MAX) ||
                    (key == "default_destination" && value.is_string() && !value.get<std::string>().empty() &&
                        std::filesystem::path(Wide(value)).is_absolute());
                if (!supported)
                { reply(Failure("invalid_request")); return; }
            }
            auto selected = choices.value("language", language);
            Change([this, choices = std::move(choices), reply]
            {
                auto document = Membership();
                document["settings"].update(choices);
                Commit(std::move(document), [this, choices, reply](StorageOutcome outcome)
                {
                    if (!outcome.saved) { reply(Failure("storage_failed", outcome.detail)); return; }
                    settings.update(choices);
                    ApplySettings();
                    reply(Success(settings));
                });
            });
            language = std::move(selected);
            return;
        }
        if (command == "preview")
        {
            if (previews.size() + parsing.size() >= 256) { reply(Failure("overloaded")); return; }
            auto source = request.at("source").get<std::string>();
            if (source.empty() || source.size() > 32768) { reply(Failure("invalid_source")); return; }
            if (_strnicmp(source.c_str(), "magnet:", 7) == 0) source.replace(0, 7, "magnet:");
            auto preview = std::make_shared<Preview>();
            preview->identity = Identity();
            preview->connection = request.value("connection_id", "");
            store.Run([preview, source]
            {
                if (source.starts_with("magnet:")) preview->params = lt::parse_magnet_uri(source);
                else
                {
                    auto bytes = Store::Read(std::filesystem::path(Wide(source)));
                    preview->params = lt::load_torrent_buffer(lt::span<char const>(bytes.data(), bytes.size()));
                }
            }, [this, preview, destination = request.value("destination", ""), reply](StorageOutcome outcome)
            {
                std::erase(parsing, preview);
                if (preview->cancelled) return;
                if (stopping) { reply(Failure("stopping")); return; }
                if (!outcome.saved) { reply(Failure("invalid_source", outcome.detail)); return; }
                auto hashes = preview->params.ti ? preview->params.ti->info_hashes() : preview->params.info_hashes;
                preview->params.info_hashes = hashes;
                for (auto& [id, existing] : previews)
                    if (existing.connection == preview->connection &&
                            ((hashes.has_v1() && existing.params.info_hashes.has_v1() && hashes.v1 == existing.params.info_hashes.v1) ||
                             (hashes.has_v2() && existing.params.info_hashes.has_v2() && hashes.v2 == existing.params.info_hashes.v2)))
                    {
                        auto const& urls = preview->params.trackers;
                        for (auto const& url : urls)
                            if (std::find(existing.params.trackers.begin(), existing.params.trackers.end(), url) == existing.params.trackers.end())
                            {
                                existing.params.trackers.push_back(url);
                                if (existing.handle.is_valid()) existing.handle.add_tracker(lt::announce_entry(url));
                            }
                        if (!existing.params.tracker_tiers.empty()) existing.params.tracker_tiers.resize(existing.params.trackers.size(), 0);
                        if (!existing.params.ti && preview->params.ti)
                        {
                            existing.params.ti = preview->params.ti;
                            if (existing.handle.is_valid()) existing.handle.set_metadata(preview->params.ti->info_section());
                        }
                        UpdatePreview(existing);
                        reply(Success(Describe(existing, destination)));
                        return;
                    }
                if (!preview->params.ti && Duplicate(hashes).empty())
                {
                    Guard(preview->params);
                    preview->params.flags &= ~lt::torrent_flags::paused;
                    preview->params.save_path = Utf8((directory / L"previews").wstring());
                    lt::error_code error;
                    preview->handle = session->add_torrent(preview->params, error);
                    if (error) { reply(Failure("preview_failed", error.message())); return; }
                }
                previews.emplace(preview->identity, *preview);
                reply(Success(Describe(*preview, destination)));
            });
            parsing.push_back(preview);
            return;
        }
        if (command == "preview_detail")
        {
            auto found = previews.find(request.at("preview_id").get<std::string>());
            if (found == previews.end() || found->second.connection != request.value("connection_id", ""))
            { reply(Failure("preview_expired")); return; }
            UpdatePreview(found->second);
            reply(Success(Describe(found->second, request.value("destination", ""))));
            return;
        }
        if (command == "cancel_preview")
        {
            auto found = previews.find(request.at("preview_id").get<std::string>());
            if (found != previews.end() && found->second.connection == request.value("connection_id", ""))
            {
                if (found->second.handle.is_valid()) session->remove_torrent(found->second.handle);
                previews.erase(found);
            }
            reply(Success());
            return;
        }
        if (command == "add")
        {
            auto previewId = request.at("preview_id").get<std::string>();
            auto found = previews.find(previewId);
            if (found == previews.end() || found->second.connection != request.value("connection_id", ""))
            { reply(Failure("preview_expired")); return; }
            UpdatePreview(found->second);
            auto duplicate = Duplicate(found->second.params.ti ? found->second.params.ti->info_hashes() : found->second.params.info_hashes);
            if (!duplicate.empty()) { reply(Success({{"torrent_id", duplicate}, {"duplicate", true}})); return; }
            auto destination = request.at("destination").get<std::string>();
            if (destination.empty() || !std::filesystem::path(Wide(destination)).is_absolute())
            { reply(Failure("invalid_destination")); return; }
            Addition addition;
            addition.identity = Identity();
            addition.params = found->second.params;
            if (!addition.params.tracker_tiers.empty()) addition.params.tracker_tiers.resize(addition.params.trackers.size(), 0);
            addition.params.save_path = destination;
            Guard(addition.params);
            addition.params.flags |= lt::torrent_flags::paused;
            Json priorities = Json::array();
            if (addition.params.ti)
            {
                priorities = request.value("priorities", Json::array());
                if (priorities.empty())
                    for (auto const& file : Files(addition.params.ti)) priorities.push_back(file.at("priority"));
                if (priorities.size() == static_cast<size_t>(addition.params.ti->num_files()))
                    for (auto index : addition.params.ti->layout().file_range())
                        if (addition.params.ti->layout().pad_file_at(index)) priorities[static_cast<int>(index)] = 0;
                if (priorities.size() != static_cast<size_t>(addition.params.ti->num_files()) ||
                    std::none_of(priorities.begin(), priorities.end(), [](auto const& value) { return value == 1 || value == 4 || value == 7; }) ||
                    std::any_of(priorities.begin(), priorities.end(), [](auto const& value) { return !value.is_number_integer() ||
                        (value != 0 && value != 1 && value != 4 && value != 7); }))
                { reply(Failure("invalid_priorities")); return; }
            }
            addition.facts = {{"torrent_id", addition.identity}, {"save_path", destination},
                {"paused", request.value("paused", false)}, {"priorities", std::move(priorities)},
                {"hashes", Hashes(addition.params.ti ? addition.params.ti->info_hashes() : addition.params.info_hashes)},
                {"forced", false}, {"added", std::chrono::system_clock::to_time_t(std::chrono::system_clock::now())}};
            addition.reply = std::move(reply);
            auto [pending, inserted] = additions.emplace(addition.identity, std::move(addition));
            if (found->second.handle.is_valid())
            {
                auto handle = found->second.handle;
                pending->second.handle = handle;
                pending->second.phase = AdditionPhase::Moving;
                handle.pause();
                handle.move_storage(destination, lt::move_flags_t::reset_save_path);
            }
            else
            {
                pending->second.params.userdata = &pending->second;
                session->async_add_torrent(pending->second.params);
            }
            previews.erase(found);
            return;
        }
        if (command == "session_pause")
        {
            auto paused = request.at("paused").get<bool>();
            Change([this, paused, reply]
            {
                auto document = Membership();
                document["settings"]["all_paused"] = paused;
                Commit(std::move(document), [this, paused, reply](StorageOutcome outcome)
                {
                    if (!outcome.saved) { reply(Failure("storage_failed", outcome.detail)); return; }
                    settings["all_paused"] = paused;
                    if (paused) session->pause();
                    else session->resume();
                    reply(Success());
                });
            });
            return;
        }
        if (command == "torrent")
        {
            auto found = torrents.find(request.at("torrent_id").get<std::string>());
            if (found == torrents.end()) { reply(Failure("torrent_removed")); return; }
            auto const& torrent = found->second;
            auto data = torrent.facts;
            auto metadata = torrent.handle.torrent_file();
            data["name"] = torrent.handle.status().name;
            data["metadata_ready"] = bool(metadata);
            data["files"] = Files(metadata);
            data["hashes"] = ContentHashes(torrent);
            lt::add_torrent_params magnet;
            magnet.ti = metadata;
            magnet.info_hashes = metadata ? metadata->info_hashes() : torrent.handle.info_hashes();
            magnet.name = data.at("name");
            for (auto const& tracker : torrent.handle.trackers()) magnet.trackers.push_back(tracker.url);
            data["magnet"] = lt::make_magnet_uri(magnet);
            if (metadata)
            {
                auto priorities = torrent.handle.get_file_priorities();
                for (auto& file : data["files"])
                {
                    auto index = file.at("index").get<size_t>();
                    if (index < priorities.size()) file["priority"] = static_cast<std::uint8_t>(priorities[index]);
                }
            }
            reply(Success(std::move(data)));
            return;
        }
        if (command == "merge_trackers")
        {
            auto preview = previews.find(request.at("preview_id").get<std::string>());
            auto id = request.at("torrent_id").get<std::string>();
            if (preview == previews.end() || preview->second.connection != request.value("connection_id", ""))
            { reply(Failure("preview_expired")); return; }
            UpdatePreview(preview->second);
            auto hashes = preview->second.params.ti ? preview->second.params.ti->info_hashes() : preview->second.params.info_hashes;
            if (Duplicate(hashes) != id) { reply(Failure("torrent_removed")); return; }
            auto urls = preview->second.params.trackers;
            Change([this, id, urls, reply]
            {
                if (!torrents.contains(id))
                { reply(Failure("torrent_removed")); committing = false; Next(); return; }
                auto document = Membership();
                auto trackers = torrents.at(id).handle.trackers();
                for (auto const& url : urls)
                    if (std::none_of(trackers.begin(), trackers.end(), [&url](auto const& entry) { return entry.url == url; }))
                        trackers.emplace_back(url);
                Json saved = Json::array();
                for (auto const& entry : trackers) saved.push_back(entry.url);
                for (auto& facts : document["torrents"])
                    if (facts.at("torrent_id") == id) facts["trackers"] = saved;
                Commit(std::move(document), [this, id, saved, trackers, reply](StorageOutcome outcome)
                {
                    if (!outcome.saved) { reply(Failure("storage_failed", outcome.detail)); return; }
                    auto& torrent = torrents.at(id);
                    torrent.facts["trackers"] = saved;
                    torrent.handle.replace_trackers(trackers);
                    torrent.unsaved = true;
                    reply(Success());
                });
            });
            return;
        }
        if (command == "pause" || command == "resume" || command == "force" ||
            command == "verify" || command == "remove" || command == "queue")
        {
            std::vector<std::string> ids = request.at("torrent_ids").get<std::vector<std::string>>();
            if (ids.empty() || ids.size() > 10000) { reply(Failure("invalid_targets")); return; }
            auto direction = request.contains("before_torrent_id") ? "before" : request.value("direction", "");
            auto before = request.contains("before_torrent_id") && !request.at("before_torrent_id").is_null() ?
                request.at("before_torrent_id").get<std::string>() : std::string{};
            if (command == "queue" && direction != "up" && direction != "down" && direction != "top" && direction != "bottom" && direction != "before")
            { reply(Failure("invalid_request")); return; }
            Change([this, ids, command, direction, before, reply]
            {
                Json document = Membership();
                for (auto const& id : ids)
                    if (!torrents.contains(id))
                    { reply(Failure("torrent_removed")); committing = false; Next(); return; }
                if (command == "verify")
                {
                    for (auto const& id : ids)
                        if (!torrents.at(id).handle.torrent_file())
                        { reply(Failure("metadata_unavailable")); committing = false; Next(); return; }
                    for (auto const& id : ids) torrents.at(id).handle.force_recheck();
                    reply(Success());
                    committing = false;
                    Next();
                    return;
                }
                if (command == "resume" || command == "force")
                    for (auto const& id : ids)
                        if (torrents.at(id).error == "alias_conflict" &&
                            !Duplicate(torrents.at(id).handle.info_hashes(), id).empty())
                        { reply(Failure("alias_conflict")); committing = false; Next(); return; }
                auto selected = [&ids](std::string const& id)
                    { return std::find(ids.begin(), ids.end(), id) != ids.end(); };
                auto order = queueOrder;
                if (command == "queue")
                {
                    for (auto const& id : ids)
                        if (static_cast<int>(torrents.at(id).handle.queue_position()) < 0)
                        { reply(Failure("invalid_targets")); committing = false; Next(); return; }
                    order = CurrentQueue();
                    if (direction == "before")
                    {
                        if ((!before.empty() && !torrents.contains(before)) || selected(before))
                        { reply(Failure("invalid_targets")); committing = false; Next(); return; }
                        std::vector<std::string> moving;
                        for (auto const& id : order) if (selected(id)) moving.push_back(id);
                        std::erase_if(order, selected);
                        order.insert(std::find(order.begin(), order.end(), before), moving.begin(), moving.end());
                    }
                    else if (direction == "top" || direction == "bottom")
                        std::stable_partition(order.begin(), order.end(), [selected, direction](auto const& id)
                            { return selected(id) == (direction == "top"); });
                    else if (direction == "up")
                    {
                        for (size_t index = 1; index < order.size(); ++index)
                            if (selected(order[index]) && !selected(order[index - 1])) std::swap(order[index], order[index - 1]);
                    }
                    else
                    {
                        for (size_t index = order.size(); index > 1; --index)
                            if (selected(order[index - 2]) && !selected(order[index - 1])) std::swap(order[index - 2], order[index - 1]);
                    }
                    document["queue_order"] = order;
                }
                else if (command == "remove")
                {
                    auto& list = document["torrents"];
                    list.erase(std::remove_if(list.begin(), list.end(), [&selected](auto const& facts)
                        { return selected(facts.at("torrent_id").template get<std::string>()); }), list.end());
                    std::erase_if(order, selected);
                    document["queue_order"] = order;
                }
                else
                for (auto& facts : document["torrents"])
                    if (std::find(ids.begin(), ids.end(), facts.at("torrent_id").get<std::string>()) != ids.end())
                    {
                        facts["paused"] = command == "pause";
                        facts["forced"] = command == "force";
                    }
                Commit(std::move(document), [this, ids, command, order, reply](StorageOutcome outcome)
                {
                    if (!outcome.saved) { reply(Failure("storage_failed", outcome.detail)); return; }
                    if (command == "queue")
                    {
                        queueOrder = order;
                        ApplyQueue();
                        reply(Success());
                        return;
                    }
                    if (command == "remove")
                    {
                        queueOrder = order;
                        for (auto const& id : ids)
                        {
                            auto found = torrents.find(id);
                            if (found == torrents.end()) continue;
                            session->remove_torrent(found->second.handle);
                            torrents.erase(found);
                        }
                        store.Run([path = directory, ids]
                        {
                            for (auto const& id : ids) std::filesystem::remove(path / Wide(id + ".resume"));
                        }, [this](StorageOutcome outcome)
                        {
                            if (!outcome.saved) Log("remove", "", "metadata_cleanup_failed");
                        });
                        reply(Success());
                        return;
                    }
                    for (auto const& id : ids)
                    {
                        auto& torrent = torrents.at(id);
                        torrent.facts["paused"] = command == "pause";
                        torrent.facts["forced"] = command == "force";
                        if (command != "pause")
                        {
                            if (torrent.error == "alias_conflict")
                            {
                                torrent.error.clear();
                                torrent.detail.clear();
                            }
                            torrent.handle.clear_error();
                            torrent.handle.unset_flags(lt::torrent_flags::upload_mode);
                            torrent.diskError.clear();
                        }
                        ApplyIntent(torrent);
                    }
                    reply(Success());
                });
            });
            return;
        }
        reply(Failure("unknown_command"));
    }

    Json Snapshot() const
    {
        Json rows = Json::array();
        int download = 0;
        int upload = 0;
        bool incoming = false;
        for (auto const& [id, torrent] : torrents)
        {
            auto status = torrent.handle.status();
            bool diskBlocked = bool(status.flags & lt::torrent_flags::upload_mode) && !status.is_seeding;
            std::string code = "downloading";
            if (status.errc || diskBlocked) code = "error";
            else if (torrent.facts.at("paused") || settings.at("all_paused")) code = "paused";
            else if (status.state == lt::torrent_status::checking_files ||
                status.state == lt::torrent_status::checking_resume_data) code = "checking";
            else if (!status.has_metadata) code = "metadata";
            else if (status.is_seeding) code = "seeding";
            else if (status.is_finished) code = "completed";
            else if (bool(status.flags & lt::torrent_flags::paused)) code = "queued";
            download += status.download_payload_rate;
            upload += status.upload_payload_rate;
            incoming |= status.has_incoming;
            auto hashes = ContentHashes(torrent);
            auto name = status.name;
            if (name.empty() && !hashes.empty()) name = hashes.front().get<std::string>();
            Json row = {{"torrent_id", id}, {"save_path", torrent.facts.at("save_path")},
                {"paused", torrent.facts.at("paused")}, {"forced", torrent.facts.value("forced", false)},
                {"added", torrent.facts.at("added")}};
            row.update({{"name", name}, {"size", status.total_wanted},
                {"progress", status.progress}, {"status", code}, {"download_rate", status.download_payload_rate},
                {"upload_rate", status.upload_payload_rate},
                {"error", torrent.error == "alias_conflict" ? torrent.error : status.errc || diskBlocked ? "torrent_error" : torrent.error},
                {"detail", status.errc ? status.errc.message() : diskBlocked ? torrent.diskError : torrent.detail},
                {"seeds", status.num_seeds}, {"peers", status.num_peers},
                {"downloaded", status.all_time_download}, {"uploaded", status.all_time_upload},
                {"queue", static_cast<int>(status.queue_position)}, {"complete", status.has_metadata && status.is_finished},
                {"incoming", status.has_incoming}, {"hashes", hashes}});
            rows.push_back(std::move(row));
        }
        auto current = settings;
        current["language"] = language;
        return {{"session_id", identity}, {"torrents", std::move(rows)}, {"settings", std::move(current)},
            {"language_saved", language == settings.at("language").get<std::string>()},
            {"download_rate", download}, {"upload_rate", upload}, {"all_paused", settings.at("all_paused")},
            {"has_incoming", incoming},
            {"stopping", stopping}, {"loading", loading}, {"storage_failed", !startupError.empty()},
            {"startup_error", startupError}};
    }

    Torrent* Find(lt::torrent_handle const& handle)
    {
        for (auto& [id, torrent] : torrents) if (torrent.handle == handle) return &torrent;
        return nullptr;
    }

    void RecordHashes(std::string const& id, Json hashes)
    {
        if (!torrents.contains(id) || torrents.at(id).facts.value("hashes", Json::array()) == hashes) return;
        if (changes.size() >= 64)
        {
            torrents.at(id).error = "storage_overloaded";
            return;
        }
        Change([this, id, hashes = std::move(hashes)]
        {
            if (!torrents.contains(id)) { committing = false; Next(); return; }
            auto document = Membership();
            for (auto& facts : document["torrents"])
                if (facts.at("torrent_id") == id) facts["hashes"] = hashes;
            Commit(std::move(document), [this, id, hashes](StorageOutcome outcome)
            {
                auto& torrent = torrents.at(id);
                if (outcome.saved) torrent.facts["hashes"] = hashes;
                else
                {
                    torrent.error = "storage_failed";
                    torrent.detail = outcome.detail;
                }
            });
        });
    }

    void Conflict(lt::torrent_conflict_alert const& alert)
    {
        bool released = false;
        for (auto& [id, preview] : previews)
            if (preview.handle == alert.handle || preview.handle == alert.conflicting_torrent)
            {
                preview.params.ti = alert.metadata;
                preview.params.info_hashes = alert.metadata->info_hashes();
                session->remove_torrent(preview.handle);
                preview.handle = {};
                released = true;
            }
        for (auto handle : {alert.handle, alert.conflicting_torrent})
            if (auto torrent = Find(handle))
            {
                RecordHashes(torrent->identity, Hashes(alert.metadata->info_hashes()));
                if (released)
                {
                    handle.clear_error();
                    ApplyIntent(*torrent);
                }
                else
                {
                    torrent->error = "alias_conflict";
                    torrent->detail = alert.message();
                }
            }
    }

    void Added(lt::add_torrent_alert const& alert)
    {
        auto pointer = alert.params.userdata.get<Addition>();
        if (!pointer) return;
        auto found = std::find_if(additions.begin(), additions.end(), [pointer](auto const& entry)
            { return &entry.second == pointer; });
        if (found == additions.end()) return;
        auto id = found->first;
        auto reply = found->second.reply;
        if (found->second.handle.is_valid()) return;
        if (alert.error)
        {
            auto duplicate = Duplicate(alert.params.info_hashes);
            reply(duplicate.empty() ? Failure("add_failed", alert.error.message()) :
                Success({{"torrent_id", duplicate}, {"duplicate", true}}));
            additions.erase(found);
            return;
        }
        SaveAddition(id, alert.handle);
    }

    void SaveAddition(std::string const& identity, lt::torrent_handle handle)
    {
        auto id = identity;
        auto reply = additions.at(id).reply;
        additions.at(id).handle = handle;
        additions.at(id).phase = AdditionPhase::Saving;
        Change([this, id, reply]
        {
            auto& addition = additions.at(id);
            auto params = addition.params;
            params.userdata = lt::client_data_t{};
            store.Write(directory / Wide(id + ".resume"), [params = std::move(params)]
                { auto bytes = lt::write_resume_data_buf(params); return std::string(bytes.begin(), bytes.end()); },
                [this, id, reply](StorageOutcome outcome)
            {
                if (!outcome.saved)
                {
                    session->remove_torrent(additions.at(id).handle);
                    additions.erase(id);
                    reply(Failure("storage_failed", outcome.detail));
                    Log("add", id, "storage_failed");
                    committing = false;
                    Next();
                    return;
                }
                auto document = Membership();
                document["torrents"].push_back(additions.at(id).facts);
                document["queue_order"].push_back(id);
                Commit(std::move(document), [this, id, reply](StorageOutcome committed)
                {
                    auto& addition = additions.at(id);
                    if (!committed.saved)
                    {
                        session->remove_torrent(addition.handle);
                        reply(Failure("storage_failed", committed.detail));
                    }
                    else
                    {
                        auto [position, inserted] = torrents.emplace(id,
                            Torrent{id, addition.handle, addition.facts});
                        queueOrder.push_back(id);
                        ApplyIntent(position->second);
                        Log("add", id, "saved");
                        reply(Success({{"torrent_id", id}, {"duplicate", false}}));
                    }
                    additions.erase(id);
                });
            });
        });
    }

    void Checkpoint(Torrent& torrent)
    {
        if (torrent.checkpoint) return;
        torrent.checkpoint = true;
        torrent.handle.save_resume_data(lt::torrent_handle::save_info_dict);
    }

    void Tick()
    {
        store.Drain();
        if (!session)
        {
            FlushLog();
            if (stopping && !loading && store.IsIdle() && shutdown)
            {
                auto completion = std::move(shutdown);
                completion(true);
            }
            return;
        }
        std::vector<lt::alert*> alerts;
        session->pop_alerts(&alerts);
        for (auto* alert : alerts)
        {
            if (auto added = lt::alert_cast<lt::add_torrent_alert>(alert)) Added(*added);
            else if (auto moved = lt::alert_cast<lt::storage_moved_alert>(alert))
            {
                auto found = std::find_if(additions.begin(), additions.end(), [moved](auto const& entry)
                    { return entry.second.phase == AdditionPhase::Moving && entry.second.handle == moved->handle; });
                if (found != additions.end()) SaveAddition(found->first, moved->handle);
            }
            else if (auto failed = lt::alert_cast<lt::storage_moved_failed_alert>(alert))
            {
                auto found = std::find_if(additions.begin(), additions.end(), [failed](auto const& entry)
                    { return entry.second.phase == AdditionPhase::Moving && entry.second.handle == failed->handle; });
                if (found != additions.end())
                {
                    found->second.reply(Failure("add_failed", failed->error.message()));
                    session->remove_torrent(found->second.handle);
                    additions.erase(found);
                }
            }
            else if (auto conflict = lt::alert_cast<lt::torrent_conflict_alert>(alert)) Conflict(*conflict);
            else if (auto received = lt::alert_cast<lt::metadata_received_alert>(alert))
            {
                for (auto& [id, preview] : previews)
                    if (preview.handle == received->handle) { UpdatePreview(preview); preview.error.clear(); }
                if (auto torrent = Find(received->handle))
                {
                    ApplyIntent(*torrent);
                    RecordHashes(torrent->identity, Hashes(received->handle.info_hashes()));
                }
            }
            else if (auto failed = lt::alert_cast<lt::metadata_failed_alert>(alert))
            {
                for (auto& [id, preview] : previews)
                    if (preview.handle == failed->handle) preview.error = failed->error.message();
            }
            else if (auto saved = lt::alert_cast<lt::save_resume_data_alert>(alert))
            {
                if (auto torrent = Find(saved->handle))
                {
                    auto id = torrent->identity;
                    store.Write(directory / Wide(id + ".resume"), [params = saved->params]
                        { auto bytes = lt::write_resume_data_buf(params); return std::string(bytes.begin(), bytes.end()); },
                        [this, id](StorageOutcome outcome)
                    {
                        auto found = torrents.find(id);
                        if (found == torrents.end()) return;
                        found->second.checkpoint = false;
                        found->second.unsaved = !outcome.saved;
                        if (outcome.saved && (found->second.error == "storage_failed" ||
                            found->second.error == "checkpoint_failed"))
                        {
                            found->second.error.clear();
                            found->second.detail.clear();
                        }
                        if (!outcome.saved)
                        {
                            if (found->second.error != "storage_failed")
                                Log("checkpoint", id, "storage_failed");
                            found->second.error = "storage_failed";
                            found->second.detail = outcome.detail;
                            found->second.retryAt = std::chrono::steady_clock::now() + std::chrono::seconds(30);
                            if (stopping) storageFailed = true;
                        }
                    });
                }
            }
            else if (auto failed = lt::alert_cast<lt::save_resume_data_failed_alert>(alert))
            {
                if (auto torrent = Find(failed->handle))
                {
                    torrent->checkpoint = false;
                    torrent->unsaved = true;
                    if (torrent->error != "checkpoint_failed")
                        Log("checkpoint", torrent->identity, "checkpoint_failed");
                    torrent->error = "checkpoint_failed";
                    torrent->detail = failed->error.message();
                    torrent->retryAt = std::chrono::steady_clock::now() + std::chrono::seconds(30);
                    if (stopping) storageFailed = true;
                }
            }
            else if (auto failed = lt::alert_cast<lt::torrent_error_alert>(alert))
            {
                if (auto torrent = Find(failed->handle))
                    Log("torrent", torrent->identity, std::to_string(failed->error.value()));
            }
            else if (auto failed = lt::alert_cast<lt::file_error_alert>(alert))
            {
                if (auto torrent = Find(failed->handle))
                {
                    torrent->diskError = failed->error.message();
                    Log("file", torrent->identity, std::to_string(failed->error.value()));
                }
            }
            else if (auto paused = lt::alert_cast<lt::torrent_paused_alert>(alert))
                std::erase(pausing, paused->handle);
            else if (lt::alert_cast<lt::alerts_dropped_alert>(alert))
            {
                if (stopping && !pausing.empty())
                {
                    storageFailed = true;
                    pausing.clear();
                    Log("shutdown", "", "recovery_required");
                }
                for (auto& [id, torrent] : torrents)
                {
                    torrent.checkpoint = false;
                    torrent.unsaved = true;
                    if (!stopping && torrent.error != "alias_conflict") ApplyIntent(torrent);
                    RecordHashes(id, Hashes(torrent.handle.info_hashes()));
                }
                auto handles = session->get_torrents();
                std::vector<std::string> failed;
                for (auto& [id, addition] : additions)
                {
                    if (addition.phase == AdditionPhase::Saving) continue;
                    if (addition.phase == AdditionPhase::Moving)
                    {
                        if (addition.handle.is_valid() && FullPath(Wide(addition.handle.status().save_path)) ==
                            FullPath(Wide(addition.params.save_path))) SaveAddition(id, addition.handle);
                        else failed.push_back(id);
                        continue;
                    }
                    auto found = std::find_if(handles.begin(), handles.end(), [&addition](lt::torrent_handle const& handle)
                        { return handle.userdata().get<Addition>() == &addition; });
                    if (found != handles.end()) SaveAddition(id, *found);
                    else failed.push_back(id);
                }
                for (auto const& id : failed)
                {
                    if (additions.at(id).handle.is_valid()) session->remove_torrent(additions.at(id).handle);
                    additions.at(id).reply(Failure("recovery_required"));
                    additions.erase(id);
                }
                Log("alerts", "", "dropped");
            }
        }
        auto now = std::chrono::steady_clock::now();
        if (now - checkpointAt >= std::chrono::seconds(30) && !stopping)
        {
            checkpointAt = now;
            for (auto& [id, torrent] : torrents)
                torrent.unsaved |= torrent.handle.need_save_resume_data();
        }
        unsigned outstanding = 0;
        for (auto const& [id, torrent] : torrents) outstanding += torrent.checkpoint;
        if (!stopping)
            for (auto& [id, torrent] : torrents)
                if (torrent.unsaved && !torrent.checkpoint && outstanding < 8 && now >= torrent.retryAt)
                { Checkpoint(torrent); ++outstanding; }
        if (stopping && !committing && changes.empty() && additions.empty())
        {
            if (!finalRequested)
            {
                if (!pauseRequested)
                {
                    pauseRequested = true;
                    pauseAt = now;
                    if (!session->is_paused())
                        for (auto const& [id, torrent] : torrents)
                            if (!bool(torrent.handle.flags() & lt::torrent_flags::paused))
                                pausing.push_back(torrent.handle);
                    session->pause();
                }
                if (!pausing.empty())
                {
                    if (now - pauseAt < std::chrono::seconds(30)) { FlushLog(); return; }
                    pausing.clear();
                    storageFailed = true;
                    Log("shutdown", "", "recovery_required");
                }
                for (auto const& [id, torrent] : torrents)
                    if (torrent.checkpoint) return;
                if (!store.IsIdle()) return;
                finalRequested = true;
                Log("shutdown", "", "checkpoint");
                for (auto& [id, torrent] : torrents) torrent.unsaved = true;
            }
            for (auto& [id, torrent] : torrents)
                if (torrent.unsaved && !torrent.checkpoint && outstanding < 8 && !storageFailed)
                { Checkpoint(torrent); ++outstanding; }
            bool pending = false;
            for (auto const& [id, torrent] : torrents)
                pending |= torrent.checkpoint || (torrent.unsaved && !storageFailed);
            if (!pending && store.IsIdle() && diagnostics.empty() && shutdown)
            {
                if (!storageFailed) session.reset();
                auto completion = std::move(shutdown);
                completion(!storageFailed);
            }
        }
        FlushLog();
    }
};

Engine::Engine(std::filesystem::path directory, std::function<void()> wake) :
    state_(std::make_unique<State>(std::move(directory), std::move(wake))) {}
Engine::~Engine() = default;
void Engine::Execute(Json const& request, std::function<void(Json)> reply)
{
    try { state_->Execute(request, reply); }
    catch (std::exception const& error) { reply(Failure("invalid_request", error.what())); }
}
void Engine::Tick() { state_->Tick(); }
Json Engine::Snapshot() const { return state_->Snapshot(); }
std::string Engine::Language() const { return state_->language; }
bool Engine::IsStopping() const { return state_->stopping; }
bool Engine::IsLoading() const { return state_->loading; }
bool Engine::HasStorageFailure() const { return !state_->startupError.empty(); }
bool Engine::ShowsAdd() const { return state_->settings.at("show_add"); }
std::string Engine::DefaultDestination() const { return state_->settings.at("default_destination"); }
void Engine::Disconnect(std::string const& connection)
{
    for (auto const& preview : state_->parsing)
        if (preview->connection == connection) preview->cancelled = true;
    std::erase_if(state_->previews, [this, &connection](auto const& entry)
    {
        if (entry.second.connection != connection) return false;
        if (entry.second.handle.is_valid()) state_->session->remove_torrent(entry.second.handle);
        return true;
    });
}
void Engine::Shutdown(std::function<void(bool)> completion)
{
    if (state_->shutdown) return;
    if (state_->stopping && state_->session)
    {
        state_->storageFailed = false;
        state_->finalRequested = false;
        state_->pauseRequested = false;
        state_->pausing.clear();
    }
    state_->stopping = true;
    for (auto const& [id, preview] : state_->previews)
        if (preview.handle.is_valid()) state_->session->remove_torrent(preview.handle);
    state_->previews.clear();
    state_->Log("shutdown", "", "requested");
    state_->shutdown = std::move(completion);
    if (!state_->session && !state_->loading)
    {
        auto callback = std::move(state_->shutdown);
        callback(true);
    }
}
}
