#include "Engine/State.h"
#include <objbase.h>
#include <libtorrent/hex.hpp>
#include <libtorrent/read_resume_data.hpp>
#include <libtorrent/session_params.hpp>
#include <libtorrent/settings_pack.hpp>
#include <algorithm>

namespace tt
{
namespace
{
constexpr std::size_t noticeLimit = 32;
constexpr auto statusInterval = std::chrono::seconds(1);
constexpr auto checkpointInterval = std::chrono::seconds(30);

// Torrent::Hashes lists the torrent's own hashes first, so the first saved
// hash of each version is its own.
lt::info_hash_t SavedHashes(std::vector<std::string> const& hashes)
{
    lt::info_hash_t result;
    for (auto const& hash : hashes)
    {
        if (hash.size() == 40 && !result.has_v1())
        {
            lt::aux::from_hex(hash, result.v1.data());
        }
        else if (hash.size() == 64 && !result.has_v2())
        {
            lt::aux::from_hex(hash, result.v2.data());
        }
    }
    return result;
}
}

Engine::State::State(std::filesystem::path path, std::function<void()> notification) :
    directory(std::move(path)), store(notification), payload(notification), wake(std::move(notification)),
    language(settings.language)
{
    // Without a saved document the engine starts from the default settings.
    auto saved = std::make_shared<Document>();
    saved->settings = settings;
    auto resumes = std::make_shared<Resumes>();
    store.Run([this, saved, resumes]
    {
        std::filesystem::create_directories(directory);
        auto file = directory / L"settings.json";
        if (!std::filesystem::exists(file))
        {
            return;
        }
        saved->Read(Json::parse(Store::Read(file)));
        for (auto const& [id, facts] : saved->torrents)
        {
            try
            {
                auto bytes = Store::Read(ResumeFile(id));
                (*resumes)[id] = lt::read_resume_data(lt::span<char const>(bytes.data(), bytes.size()));
            }
            catch (std::exception const&)
            {
                // Start adds this torrent again from its saved hashes.
            }
        }
    }, [this, saved, resumes](StorageOutcome outcome)
    {
        if (!outcome.succeeded)
        {
            startupError = outcome.detail;
            loading = false;
            diagnostics.Write("startup", "", "storage_failed");
            return;
        }
        Start(*saved, *resumes);
    });
}

std::string Engine::State::Identity()
{
    GUID guid;
    if (FAILED(CoCreateGuid(&guid)))
    {
        throw std::runtime_error("Cannot create identity");
    }
    wchar_t text[40];
    StringFromGUID2(guid, text, static_cast<int>(std::size(text)));
    return Utf8(text);
}

bool Engine::State::Contains(std::vector<std::string> const& values, std::string const& value)
{
    return std::find(values.begin(), values.end(), value) != values.end();
}

void Engine::State::Start(Document const& saved, Resumes& resumes)
{
    settings = saved.settings;
    language = settings.language;
    lt::settings_pack pack;
    pack.set_str(lt::settings_pack::listen_interfaces, "");
    pack.set_bool(lt::settings_pack::enable_upnp, false);
    pack.set_bool(lt::settings_pack::enable_natpmp, false);
    pack.set_int(lt::settings_pack::alert_mask,
        lt::alert_category::error | lt::alert_category::storage | lt::alert_category::status);
    session = std::make_unique<lt::session>(lt::session_params(pack));
    if (wake)
    {
        session->set_alert_notify(wake);
    }
    auto classes = session->get_peer_class_type_filter();
    for (int type = 0; type < lt::peer_class_type_filter::num_socket_types; ++type)
    {
        classes.add(static_cast<lt::peer_class_type_filter::socket_type_t>(type),
            lt::session::global_peer_class_id);
    }
    session->set_peer_class_type_filter(classes);
    RefreshPolicy(true);
    for (auto const& [id, facts] : saved.torrents)
    {
        lt::add_torrent_params params;
        if (auto found = resumes.find(id); found != resumes.end())
        {
            params = std::move(found->second);
            if (facts.verifyFiles || !SameFolder(params.save_path, facts.savePath))
            {
                params.have_pieces.clear();
                params.verified_pieces.clear();
                params.unfinished_pieces.clear();
                params.flags &= ~(lt::torrent_flags::seed_mode | lt::torrent_flags::no_verify_files);
            }
        }
        else
        {
            // Like a magnet link, the torrent fetches its metadata from peers
            // and then checks the files already on disk. Without saved hashes
            // it cannot come back, and only this log line keeps its identity.
            diagnostics.Write("startup", id, "resume_unreadable");
            params.info_hashes = SavedHashes(facts.hashes);
            if (!params.info_hashes.has_v1() && !params.info_hashes.has_v2())
            {
                continue;
            }
        }
        params.save_path = facts.savePath;
        if (!facts.priorities.empty())
        {
            params.file_priorities = facts.priorities;
            params.piece_priorities.clear();
        }
        params.flags &= ~lt::torrent_flags::auto_managed;
        params.flags |= lt::torrent_flags::paused;
        auto handle = session->add_torrent(params);
        Install(id, handle, facts, params);
        if (facts.trackers)
        {
            handle.replace_trackers(*facts.trackers);
        }
    }
    queueOrder = saved.queueOrder;
    ApplyQueue();
    for (auto& [id, torrent] : torrents)
    {
        torrent.ApplyIntent();
    }
    loading = false;
    diagnostics.Write("startup", "", "ready");
}

std::filesystem::path Engine::State::ResumeFile(std::string const& id) const
{
    return directory / Wide(id + ".resume");
}

Json Engine::State::Document::ToJson() const
{
    Json list = Json::array();
    for (auto const& [id, facts] : torrents)
    {
        auto entry = facts.ToJson();
        entry["torrent_id"] = id;
        list.push_back(std::move(entry));
    }
    return {{"format", format}, {"settings", settings.ToJson()}, {"torrents", std::move(list)},
        {"queue_order", queueOrder}};
}

void Engine::State::Document::Read(Json const& saved)
{
    if (saved.at("format") != format)
    {
        throw std::runtime_error("Unsupported store format");
    }
    settings.Read(saved.at("settings"));
    for (auto const& entry : saved.at("torrents"))
    {
        torrents.emplace(entry.at("torrent_id").get<std::string>(), Facts::Read(entry));
    }
    queueOrder = saved.value("queue_order", std::vector<std::string>{});
}

// The state that this document is built from changes only in a commit's
// completion, after the write succeeds.
Engine::State::Document Engine::State::Saved() const
{
    Document document;
    document.settings = settings;
    for (auto const& [id, torrent] : torrents)
    {
        document.torrents.emplace(id, torrent.facts);
    }
    document.queueOrder = queueOrder;
    return document;
}

std::string Engine::State::Duplicate(lt::info_hash_t const& hashes, std::string const& excluded) const
{
    auto values = Hashes(hashes);
    for (auto const& [id, torrent] : torrents)
    {
        if (id != excluded && Overlaps(values, torrent.Hashes()))
        {
            return id;
        }
    }
    return {};
}

bool Engine::State::Overlaps(std::vector<std::string> const& hashes, std::vector<std::string> const& others)
{
    return std::any_of(hashes.begin(), hashes.end(),
        [&others](std::string const& hash) { return Contains(others, hash); });
}

tt::Activity Engine::State::Activity() const
{
    tt::Activity activity;
    for (auto const& [id, torrent] : torrents)
    {
        activity.downloadRate += torrent.status.download_payload_rate;
        activity.uploadRate += torrent.status.upload_payload_rate;
        activity.hasIncoming |= torrent.status.has_incoming;
        switch (torrent.Classify(IsPaused()))
        {
        case Status::Downloading:
            activity.downloading = true;
            ++activity.active;
            break;
        case Status::Seeding:
        case Status::Completed:
            activity.seeding = true;
            ++activity.active;
            break;
        case Status::Metadata:
        case Status::Checking:
            ++activity.active;
            break;
        case Status::Queued:
            ++activity.queued;
            break;
        case Status::Error:
        case Status::Paused:
        case Status::Moving:
            break;
        }
    }
    activity.downloading = activity.downloading && !stopping;
    activity.seeding = activity.seeding && !stopping;
    activity.torrentCount = torrents.size();
    activity.allPaused = IsPaused();
    activity.missingInterface = interfaceMissing ? settings.networkInterface : std::string();
    activity.notificationsEnabled = settings.notificationsEnabled;
    activity.preventSleep = settings.preventSleep;
    activity.preventSleepSeeding = settings.preventSleepSeeding;
    activity.backgroundNoticeShown = settings.backgroundNoticeShown;
    activity.filesBusy = FilesBusy();
    return activity;
}

Json Engine::State::Snapshot() const
{
    Json rows = Json::array();
    for (auto const& [id, torrent] : torrents)
    {
        rows.push_back(torrent.Row(IsPaused()));
    }
    auto current = settings.ToJson();
    current["language"] = language;
    auto activity = Activity();
    return {{"session_id", sessionId}, {"torrents", std::move(rows)}, {"settings", std::move(current)},
        {"language_saved", language == settings.language},
        {"download_rate", activity.downloadRate}, {"upload_rate", activity.uploadRate},
        {"all_paused", IsPaused()},
        {"alternative_limits", UsesAlternative()},
        {"missing_interface", activity.missingInterface},
        {"has_incoming", activity.hasIncoming},
        {"stopping", stopping}, {"loading", loading}, {"storage_failed", !startupError.empty()},
        {"startup_error", startupError}};
}

Torrent* Engine::State::Find(lt::torrent_handle const& handle)
{
    auto found = handles.find(handle);
    return found == handles.end() ? nullptr : found->second;
}

Torrent& Engine::State::Install(std::string const& id, lt::torrent_handle handle, Facts facts,
    lt::add_torrent_params const& params)
{
    auto& torrent = torrents.emplace(id, Torrent{id, handle, std::move(facts)}).first->second;
    torrent.comment = params.comment;
    torrent.creator = params.created_by;
    torrent.created = params.creation_date;
    torrent.status = handle.status(lt::torrent_handle::query_name);
    torrent.savedUploaded = torrent.status.all_time_upload;
    handles.emplace(handle, &torrent);
    return torrent;
}

void Engine::State::Notify(NoticeKind kind, Torrent const& torrent, std::string detail)
{
    Notify(kind, torrent.Name(), std::move(detail), torrent.identity);
}

void Engine::State::Notify(NoticeKind kind, std::string name, std::string detail, std::string id)
{
    diagnostics.Write("notification", id, ToString(kind));
    if (notices.size() == noticeLimit)
    {
        notices.erase(notices.begin());
    }
    notices.push_back(
        {.kind = kind, .name = std::move(name), .detail = std::move(detail), .torrentId = std::move(id)});
}

void Engine::State::RecordHashes(Torrent& torrent, lt::info_hash_t const& hashes)
{
    torrent.status.info_hashes = hashes;
    RecordHashes(torrent);
}

// Saves every hash the torrent is known by, so that a later addition of
// the same content is found as a duplicate.
void Engine::State::RecordHashes(Torrent& torrent)
{
    auto id = torrent.identity;
    auto hashes = torrent.Hashes();
    if (torrent.facts.hashes == hashes)
    {
        torrent.hashError.reset();
        return;
    }
    if (!changes.Queue([this, id, hashes = std::move(hashes)]
    {
        if (!torrents.contains(id))
        {
            return;
        }
        auto document = Saved();
        document.torrents.at(id).hashes = hashes;
        changes.Commit(document.ToJson(), [this, id, hashes](StorageOutcome outcome)
        {
            auto& torrent = torrents.at(id);
            if (outcome.succeeded)
            {
                torrent.facts.hashes = hashes;
                torrent.hashError.reset();
            }
            else
            {
                torrent.hashError = Problem{ProblemKind::StorageFailed, outcome.detail};
                if (stopping)
                {
                    saveFailure = outcome.detail;
                }
            }
        });
    }))
    {
        torrent.hashError = Problem{ProblemKind::StorageOverloaded};
        if (stopping)
        {
            saveFailure.emplace();
        }
    }
}

void Engine::State::Tick()
{
    store.Drain();
    payload.Drain();
    if (session)
    {
        std::vector<lt::alert*> alerts;
        session->pop_alerts(&alerts);
        for (auto* alert : alerts)
        {
            Handle(alert);
        }
        if (!stopping)
        {
            Maintain();
        }
    }
    if (stopping)
    {
        Stop();
    }
    diagnostics.Flush();
}

void Engine::State::Maintain()
{
    auto now = std::chrono::steady_clock::now();
    if (now - statusAt >= statusInterval)
    {
        statusAt = now;
        RefreshPolicy();
        LimitSeeds();
        SampleHistory();
        for (auto& [id, torrent] : torrents)
        {
            CompletePriorities(torrent);
        }
        session->post_torrent_updates(lt::torrent_handle::query_name);
    }
    if (now - checkpointAt >= checkpointInterval)
    {
        checkpointAt = now;
        for (auto& [id, torrent] : torrents)
        {
            torrent.unsaved |= torrent.IsChanged();
            if (torrent.hashError)
            {
                RecordHashes(torrent);
            }
        }
    }
    CheckpointUnsaved();
}
}
