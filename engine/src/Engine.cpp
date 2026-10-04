#include "Engine.h"
#include "Store.h"

#include <Windows.h>
#include <ShlObj.h>
#include <objbase.h>
#include <libtorrent/add_torrent_params.hpp>
#include <libtorrent/alert_types.hpp>
#include <libtorrent/magnet_uri.hpp>
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
        bool cancelled = false;
    };

    struct Addition
    {
        std::string identity;
        lt::add_torrent_params params;
        Json facts;
        std::function<void(Json)> reply;
        lt::torrent_handle handle;
    };

    std::filesystem::path directory;
    Store store;
    std::function<void()> wake;
    std::unique_ptr<lt::session> session;
    std::string identity = Identity();
    Json settings;
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
            {"port_mapping", true}, {"listen_port", 6881}};
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
                    ApplyIntent(torrents.at(id));
                }
            loading = false;
            Log("startup", "", "ready");
        });
    }

    Json Membership() const
    {
        Json list = Json::array();
        for (auto const& [id, torrent] : torrents) list.push_back(torrent.facts);
        return {{"format", 1}, {"settings", settings}, {"torrents", std::move(list)}};
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
        session->apply_settings(pack);
    }

    void ApplyIntent(Torrent& torrent)
    {
        torrent.handle.unset_flags(lt::torrent_flags::default_dont_download);
        if (torrent.facts.contains("priorities"))
        {
            std::vector<lt::download_priority_t> priorities;
            for (auto const& priority : torrent.facts.at("priorities"))
                priorities.emplace_back(priority.get<std::uint8_t>());
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
            torrent.handle.set_flags(lt::torrent_flags::auto_managed);
            torrent.handle.resume();
        }
        torrent.unsaved = true;
    }

    std::string Duplicate(lt::info_hash_t const& hashes) const
    {
        for (auto const& [id, torrent] : torrents)
        {
            auto existing = torrent.handle.info_hashes();
            if ((hashes.has_v1() && existing.has_v1() && hashes.v1 == existing.v1) ||
                (hashes.has_v2() && existing.has_v2() && hashes.v2 == existing.v2)) return id;
        }
        return {};
    }

    Json Describe(Preview const& preview) const
    {
        auto const& params = preview.params;
        Json files = Json::array();
        std::int64_t size = 0;
        std::string name = params.name;
        if (params.ti)
        {
            name = params.ti->name();
            size = params.ti->total_size();
            auto const& storage = params.ti->layout();
            for (auto index : storage.file_range())
                files.push_back({{"index", static_cast<int>(index)},
                    {"path", storage.file_path(index)}, {"size", storage.file_size(index)}});
        }
        return {{"preview_id", preview.identity}, {"name", name}, {"size", size},
            {"files", std::move(files)}, {"duplicate", Duplicate(params.info_hashes)}};
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
                    (key == "theme" && (value == "system" || value == "light" || value == "dark"));
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
            }, [this, preview, reply](StorageOutcome outcome)
            {
                std::erase(parsing, preview);
                if (preview->cancelled) return;
                if (!outcome.saved) { reply(Failure("invalid_source", outcome.detail)); return; }
                previews.emplace(preview->identity, *preview);
                reply(Success(Describe(*preview)));
            });
            parsing.push_back(preview);
            return;
        }
        if (command == "cancel_preview")
        {
            auto found = previews.find(request.at("preview_id").get<std::string>());
            if (found != previews.end() && found->second.connection == request.value("connection_id", ""))
                previews.erase(found);
            reply(Success());
            return;
        }
        if (command == "add")
        {
            auto previewId = request.at("preview_id").get<std::string>();
            auto found = previews.find(previewId);
            if (found == previews.end() || found->second.connection != request.value("connection_id", ""))
            { reply(Failure("preview_expired")); return; }
            auto duplicate = Duplicate(found->second.params.info_hashes);
            if (!duplicate.empty()) { reply(Success({{"torrent_id", duplicate}, {"duplicate", true}})); return; }
            auto destination = request.at("destination").get<std::string>();
            if (destination.empty() || !std::filesystem::path(Wide(destination)).is_absolute())
            { reply(Failure("invalid_destination")); return; }
            Addition addition;
            addition.identity = Identity();
            addition.params = found->second.params;
            addition.params.save_path = destination;
            addition.params.flags &= ~(lt::torrent_flags::auto_managed | lt::torrent_flags::share_mode);
            addition.params.flags |= lt::torrent_flags::paused | lt::torrent_flags::default_dont_download |
                lt::torrent_flags::duplicate_is_error;
            addition.params.piece_priorities.clear();
            addition.params.file_priorities.assign(addition.params.ti ? addition.params.ti->num_files() : 0,
                lt::dont_download);
            Json priorities = Json::array();
            if (addition.params.ti)
                for (int index = 0; index < addition.params.ti->num_files(); ++index) priorities.push_back(4);
            addition.facts = {{"torrent_id", addition.identity}, {"save_path", destination},
                {"paused", request.value("paused", false)}, {"priorities", std::move(priorities)},
                {"added", std::chrono::system_clock::to_time_t(std::chrono::system_clock::now())}};
            addition.reply = std::move(reply);
            auto [pending, inserted] = additions.emplace(addition.identity, std::move(addition));
            pending->second.params.userdata = &pending->second;
            session->async_add_torrent(pending->second.params);
            previews.erase(found);
            return;
        }
        if (command == "pause" || command == "resume")
        {
            std::vector<std::string> ids = request.at("torrent_ids").get<std::vector<std::string>>();
            if (ids.empty() || ids.size() > 10000) { reply(Failure("invalid_targets")); return; }
            Change([this, ids, command, reply]
            {
                Json document = Membership();
                for (auto const& id : ids)
                    if (!torrents.contains(id))
                    { reply(Failure("torrent_removed")); committing = false; Next(); return; }
                for (auto& facts : document["torrents"])
                    if (std::find(ids.begin(), ids.end(), facts.at("torrent_id").get<std::string>()) != ids.end())
                        facts["paused"] = command == "pause";
                Commit(std::move(document), [this, ids, command, reply](StorageOutcome outcome)
                {
                    if (!outcome.saved) { reply(Failure("storage_failed", outcome.detail)); return; }
                    for (auto const& id : ids)
                    {
                        auto& torrent = torrents.at(id);
                        torrent.facts["paused"] = command == "pause";
                        if (command == "resume")
                        {
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
        for (auto const& [id, torrent] : torrents)
        {
            auto status = torrent.handle.status();
            bool diskBlocked = bool(status.flags & lt::torrent_flags::upload_mode) && !status.is_seeding;
            std::string code = "downloading";
            if (status.errc || diskBlocked) code = "error";
            else if (torrent.facts.at("paused")) code = "paused";
            else if (status.state == lt::torrent_status::checking_files ||
                status.state == lt::torrent_status::checking_resume_data) code = "checking";
            else if (!status.has_metadata) code = "metadata";
            else if (status.is_seeding) code = "seeding";
            else if (status.is_finished) code = "completed";
            else if (bool(status.flags & lt::torrent_flags::paused)) code = "queued";
            download += status.download_payload_rate;
            upload += status.upload_payload_rate;
            Json row = torrent.facts;
            row.update({{"name", status.name}, {"size", status.total_wanted},
                {"progress", status.progress}, {"status", code}, {"download_rate", status.download_payload_rate},
                {"upload_rate", status.upload_payload_rate},
                {"error", status.errc || diskBlocked ? "torrent_error" : torrent.error},
                {"detail", status.errc ? status.errc.message() : diskBlocked ? torrent.diskError : torrent.detail},
                {"seeds", status.num_seeds}, {"peers", status.num_peers},
                {"downloaded", status.all_time_download}, {"uploaded", status.all_time_upload},
                {"queue", static_cast<int>(status.queue_position)}, {"complete", status.is_finished},
                {"incoming", status.has_incoming}});
            rows.push_back(std::move(row));
        }
        auto current = settings;
        current["language"] = language;
        return {{"session_id", identity}, {"torrents", std::move(rows)}, {"settings", std::move(current)},
            {"language_saved", language == settings.at("language").get<std::string>()},
            {"download_rate", download}, {"upload_rate", upload}, {"all_paused", false},
            {"stopping", stopping}, {"loading", loading}, {"storage_failed", !startupError.empty()},
            {"startup_error", startupError}};
    }

    Torrent* Find(lt::torrent_handle const& handle)
    {
        for (auto& [id, torrent] : torrents) if (torrent.handle == handle) return &torrent;
        return nullptr;
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
                        ApplyIntent(position->second);
                        Log("add", id, "saved");
                        reply(Success({{"torrent_id", id}}));
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
                }
                auto handles = session->get_torrents();
                std::vector<std::string> failed;
                for (auto& [id, addition] : additions)
                {
                    if (addition.handle.is_valid()) continue;
                    auto found = std::find_if(handles.begin(), handles.end(), [&addition](lt::torrent_handle const& handle)
                        { return handle.userdata().get<Addition>() == &addition; });
                    if (found != handles.end()) SaveAddition(id, *found);
                    else failed.push_back(id);
                }
                for (auto const& id : failed)
                {
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
void Engine::Disconnect(std::string const& connection)
{
    for (auto const& preview : state_->parsing)
        if (preview->connection == connection) preview->cancelled = true;
    std::erase_if(state_->previews, [&connection](auto const& entry)
        { return entry.second.connection == connection; });
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
    state_->Log("shutdown", "", "requested");
    state_->shutdown = std::move(completion);
    if (!state_->session && !state_->loading)
    {
        auto callback = std::move(state_->shutdown);
        callback(true);
    }
}
}
