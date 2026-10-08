#include "Engine/State.h"
#include <objbase.h>
#include <libtorrent/hex.hpp>
#include <libtorrent/read_resume_data.hpp>
#include <libtorrent/session_params.hpp>
#include <libtorrent/settings_pack.hpp>
#include <libtorrent/fingerprint.hpp>
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

Engine::State::State(std::filesystem::path path, std::function<void()> wake) :
    directory(std::move(path)), store(wake), payload(wake), sources(wake), checks(wake), wake(std::move(wake)),
    language(settings.language)
{
    // Without a saved document the engine starts from the default settings.
    auto saved = std::make_shared<Document>();
    saved->settings = settings;
    store.Run([this, saved]
    {
        std::filesystem::create_directories(directory);
        auto file = directory / L"settings.json";
        if (!std::filesystem::exists(file))
        {
            return;
        }
        saved->Read(Json::parse(Store::Read(file)));
    }, [this, saved](StorageOutcome outcome)
    {
        if (!outcome.succeeded)
        {
            startupError = outcome.detail;
            startup = Startup::Ready;
            log.Write("startup", "", "storage_failed");
            return;
        }
        settings = saved->settings;
        language = settings.language;
        startup = Startup::Transfers;
        // Queued only now, so the settings completion drains alone and the
        // desktop shows the splash before Start loads the transfers on the
        // same thread.
        auto resumes = std::make_shared<Resumes>();
        store.Run([this, saved, resumes]
        {
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
                startup = Startup::Ready;
                log.Write("startup", "", "storage_failed");
                return;
            }
            Start(*saved, *resumes);
        });
    });
}

std::string Engine::State::NewId()
{
    GUID guid;
    if (FAILED(CoCreateGuid(&guid)))
    {
        throw std::runtime_error("Cannot create identity");
    }
    wchar_t text[40];
    if (!StringFromGUID2(guid, text, static_cast<int>(std::size(text))))
    {
        throw std::runtime_error("Cannot create identity");
    }
    return Utf8(text);
}

bool Engine::State::Contains(std::vector<std::string> const& values, std::string const& value)
{
    return std::find(values.begin(), values.end(), value) != values.end();
}

void Engine::State::Start(Document const& saved, Resumes& resumes)
{
    lt::settings_pack pack;
    pack.set_str(lt::settings_pack::listen_interfaces, "");
    pack.set_bool(lt::settings_pack::enable_upnp, false);
    pack.set_bool(lt::settings_pack::enable_natpmp, false);
    pack.set_str(lt::settings_pack::user_agent, "TinyTorrent/" TT_VERSION);
    pack.set_str(lt::settings_pack::peer_fingerprint,
        lt::generate_fingerprint("TY", TT_VERSION_MAJOR, TT_VERSION_MINOR, TT_VERSION_BUILD));
    pack.set_str(lt::settings_pack::dht_bootstrap_nodes,
        "dht.libtorrent.org:25401,dht.transmissionbt.com:6881,router.bittorrent.com:6881");
    pack.set_int(lt::settings_pack::alert_mask,
        lt::alert_category::error | lt::alert_category::storage | lt::alert_category::status |
        lt::alert_category::file_progress);
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
    for (auto [id, facts] : saved.torrents)
    {
        lt::add_torrent_params params;
        auto found = resumes.find(id);
        // A record that lost its folder takes the one its resume file holds.
        if (facts.savePath.empty())
        {
            facts.savePath = found != resumes.end() && !found->second.save_path.empty() ?
                found->second.save_path : saved.settings.destination;
        }
        if (found != resumes.end())
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
            log.Write("startup", id, "resume_unreadable");
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
        }
        // Piece priorities follow from the saved file priorities and
        // firstLast, so a resume file older than those choices cannot keep
        // first and last pieces raised.
        params.piece_priorities.clear();
        params.flags &= ~lt::torrent_flags::auto_managed;
        params.flags |= lt::torrent_flags::paused;
        params.flags |= lt::torrent_flags::duplicate_is_error;
        params.flags |= lt::torrent_flags::default_dont_download;
        if (!params.ti)
            std::fill(params.file_priorities.begin(), params.file_priorities.end(), lt::dont_download);
        Restore(id, facts, std::move(params));
    }
    queueOrder = saved.queueOrder;
    ApplyQueue();
    for (auto& [id, torrent] : torrents)
    {
        torrent.ApplyIntent();
    }
    startup = Startup::Ready;
    log.Write("startup", "", "ready");
}

void Engine::State::Restore(std::string const& id, Facts facts, lt::add_torrent_params params)
{
    lt::error_code error;
    auto handle = session->add_torrent(params, error);
    if (error == lt::errors::duplicate_torrent)
    {
        auto hashes = params.ti ? params.ti->info_hashes() : params.info_hashes;
        auto& torrent = torrents.insert_or_assign(id, Torrent{id, {}, std::move(facts)}).first->second;
        torrent.restore = std::move(params);
        torrent.status.info_hashes = hashes;
        torrent.unsaved = false;
        for (auto& [otherId, other] : torrents)
        {
            if (Overlaps(torrent.Hashes(), other.Hashes()))
            {
                other.conflict = error.message();
                other.ApplyIntent();
            }
        }
        log.Write("startup", id, "alias_conflict");
        return;
    }
    if (error)
    {
        throw lt::system_error(error);
    }
    auto& torrent = Install(id, handle, std::move(facts), params);
    torrent.namePhase = NamePhase::Pending;
    PrepareFiles(torrent);
    if (torrent.facts.trackers)
    {
        handle.replace_trackers(*torrent.facts.trackers);
    }
}

void Engine::State::RestorePending()
{
    if (FilesBusy() || !additions.empty())
    {
        return;
    }
    for (auto& [id, torrent] : torrents)
    {
        if (!torrent.restore || torrent.deleted || !FindDuplicate(torrent.status.info_hashes, id).empty())
        {
            continue;
        }
        Restore(id, torrent.facts, std::move(*torrent.restore));
        torrent.ApplyIntent();
        ApplyQueue();
    }
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
    return {{"format", format}, {"settings", settings.ToFile()}, {"torrents", std::move(list)},
        {"queue_order", queueOrder}};
}

// Refuses a document of another format, which a save must not rewrite, or
// without a readable list of torrents, which a save would drop. Every other
// damaged value is repaired.
void Engine::State::Document::Read(Json const& saved)
{
    if (saved.at("format") != format)
    {
        throw std::runtime_error("Unsupported store format");
    }
    if (!saved.at("torrents").is_array())
    {
        throw std::runtime_error("Unreadable torrent list");
    }
    if (auto found = saved.find("settings"); found != saved.end())
    {
        settings.Read(*found);
    }
    for (auto const& entry : saved.at("torrents"))
    {
        // Without its identity a record cannot find its resume file, so it
        // comes back from its saved hashes under a new one.
        auto id = ReadSaved(entry, "torrent_id", std::string());
        torrents.emplace(id.empty() ? NewId() : id, Facts::Read(entry));
    }
    if (auto found = saved.find("queue_order"); found != saved.end() && found->is_array())
    {
        for (auto const& id : *found)
        {
            if (id.is_string())
            {
                queueOrder.push_back(id.get<std::string>());
            }
        }
    }
}

// The state that this document is built from changes only in a commit's
// completion, after the write succeeds.
Engine::State::Document Engine::State::Saved() const
{
    Document document;
    document.settings = settings;
    for (auto const& [id, torrent] : torrents)
    {
        // A deleted torrent is out of the saved list; it stays in torrents
        // only until it is removed.
        if (!torrent.deleted)
        {
            document.torrents.emplace(id, torrent.facts);
        }
    }
    document.queueOrder = queueOrder;
    std::erase_if(document.queueOrder, [&document](auto const& id) { return !document.torrents.contains(id); });
    return document;
}

std::string Engine::State::FindDuplicate(lt::info_hash_t const& hashes, std::string const& excluded) const
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
        if (torrent.deleted)
        {
            continue;
        }
        ++activity.torrentCount;
        activity.downloadRate += torrent.status.download_payload_rate;
        activity.uploadRate += torrent.status.upload_payload_rate;
        activity.hasIncoming |= torrent.status.has_incoming;
        if (torrent.Diagnose())
        {
            ++activity.errorCount;
        }
        switch (torrent.Classify(IsPaused()))
        {
        case Status::Downloading:
            activity.downloading = true;
            ++activity.activeCount;
            break;
        case Status::Seeding:
        case Status::Completed:
            activity.seeding = true;
            ++activity.activeCount;
            break;
        case Status::Metadata:
        case Status::Checking:
            ++activity.activeCount;
            break;
        case Status::Queued:
            ++activity.queuedCount;
            break;
        case Status::Error:
        case Status::Paused:
        case Status::AllPaused:
        case Status::Moving:
            break;
        }
    }
    activity.downloading = activity.downloading && !shuttingDown;
    activity.seeding = activity.seeding && !shuttingDown;
    activity.paused = IsPaused();
    activity.pausedByChoice = IsPausedByChoice();
    activity.missingAdapter = adapterMissing ? settings.networkAdapter : std::string();
    activity.notificationsEnabled = settings.notificationsEnabled;
    activity.notifiesProblems = settings.notifiesProblems;
    activity.notifiesAdded = settings.notifiesAdded;
    activity.preventsSleep = settings.preventsSleep;
    activity.preventsSleepSeeding = settings.preventsSleepSeeding;
    activity.backgroundNoticeShown = settings.backgroundNoticeShown;
    activity.reportedPrograms = settings.reportedPrograms;
    activity.filesBusy = FilesBusy();
    activity.confirmsExit = settings.confirmsExit;
    return activity;
}

Json Engine::State::Snapshot() const
{
    Json rows = Json::array();
    for (auto const& [id, torrent] : torrents)
    {
        if (!torrent.deleted)
        {
            rows.push_back(torrent.Row(IsPaused()));
        }
    }
    auto current = settings.ToJson();
    current["language"] = language;
    auto activity = Activity();
    auto mode = CurrentLimits();
    auto caps = settings.Caps(mode);
    auto origin = !settings.scheduleEnabled ? "manual" : limitOverride ? "override" : "schedule";
    // A pause that Resume all lifts comes first, so "adapter" means that only the
    // missing adapter pauses transfers and the window offers Settings instead.
    auto pause = !IsPaused() ? "" : settings.allPaused ? "manual" : IsPausedByChoice() ? "schedule" : "adapter";
    return {{"session_id", sessionId}, {"torrents", std::move(rows)}, {"settings", std::move(current)},
        {"language_saved", language == settings.language},
        {"download_rate", activity.downloadRate}, {"upload_rate", activity.uploadRate},
        {"session_paused", IsPaused()},
        {"limits", {{"origin", origin}, {"mode", Settings::Name(mode)},
            {"download", caps.download}, {"upload", caps.upload}, {"pause", pause}}},
        {"missing_adapter", activity.missingAdapter},
        {"has_incoming", activity.hasIncoming},
        {"external_ipv4", adapterMissing ? "" : externalIpv4},
        {"external_ipv6", adapterMissing ? "" : externalIpv6},
        {"proxy", proxyOutcome ? Json(std::string(Name(*proxyOutcome))) : Json()},
        {"proxy_check", !requestedCheck ? Json() : Json{{"check_id", requestedCheck->checkId},
            {"outcome", requestedCheck->result ? Json(std::string(Name(requestedCheck->result->outcome))) : Json()},
            {"milliseconds", requestedCheck->result ? Json(requestedCheck->result->elapsed.count()) : Json()}}},
        {"shutting_down", shuttingDown}, {"loading", startup != Startup::Ready}, {"storage_failed", !startupError.empty()},
        {"startup_error", startupError}};
}

Json Engine::State::History(bool day) const
{
    return {{"session_id", sessionId}, {"samples", history.Read(day)}};
}

Torrent* Engine::State::Find(lt::torrent_handle const& handle)
{
    auto found = handles.find(handle);
    return found == handles.end() ? nullptr : found->second;
}

Torrent& Engine::State::Install(std::string const& id, lt::torrent_handle handle, Facts facts,
    lt::add_torrent_params const& params)
{
    auto& torrent = torrents.insert_or_assign(id, Torrent{id, handle, std::move(facts)}).first->second;
    torrent.comment = params.comment;
    torrent.creator = params.created_by;
    torrent.created = params.creation_date;
    torrent.status = handle.status(lt::torrent_handle::query_name);
    torrent.namePhase = params.ti ? NamePhase::Ready : NamePhase::Pending;
    CompleteFiles(torrent);
    torrent.savedUploaded = torrent.status.all_time_upload;
    handles.emplace(handle, &torrent);
    return torrent;
}

void Engine::State::Notify(NoticeKind kind, Torrent const& torrent, std::string detail, std::string code)
{
    if (torrent.deleted && kind != NoticeKind::Error)
    {
        return;
    }
    Notify(kind, torrent.Name(), std::move(detail), torrent.torrentId, std::move(code));
}

void Engine::State::Notify(NoticeKind kind, std::string name, std::string detail, std::string torrentId,
    std::string code)
{
    log.Write("notification", torrentId, ToString(kind));
    if (notices.size() == noticeLimit)
    {
        notices.erase(notices.begin());
    }
    notices.push_back(
        {.kind = kind, .name = std::move(name), .detail = std::move(detail), .code = std::move(code),
            .torrentId = std::move(torrentId)});
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
    auto id = torrent.torrentId;
    auto hashes = torrent.Hashes();
    if (torrent.facts.hashes == hashes)
    {
        torrent.hashError.reset();
        return;
    }
    if (!changes.Queue([this, id, hashes = std::move(hashes)]
    {
        if (!torrents.contains(id) || torrents.at(id).deleted)
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
                if (shuttingDown)
                {
                    saveFailure = outcome.detail;
                }
            }
        });
    }))
    {
        torrent.hashError = Problem{ProblemKind::StorageOverloaded};
        if (shuttingDown)
        {
            saveFailure.emplace();
        }
    }
}

Engine::State::~State()
{
    sources.Abandon();
    checkStop.request_stop();
    checks.Abandon();
}

void Engine::State::Tick()
{
    store.Drain();
    payload.Drain();
    sources.Drain();
    checks.Drain();
    if (session)
    {
        std::vector<lt::alert*> alerts;
        session->pop_alerts(&alerts);
        for (auto* alert : alerts)
        {
            Handle(alert);
        }
        ContinueRename();
        RemoveDeferred();
        if (!shuttingDown)
        {
            Maintain();
        }
    }
    if (shuttingDown)
    {
        ContinueShutdown();
    }
    log.Flush();
}

void Engine::State::Maintain()
{
    auto now = std::chrono::steady_clock::now();
    if (now - statusAt >= statusInterval)
    {
        statusAt = now;
        RestorePending();
        RefreshPolicy();
        LimitSeeds();
        auto time = std::chrono::system_clock::to_time_t(std::chrono::system_clock::now());
        auto activity = Activity();
        history.Sample(time, double(activity.downloadRate), double(activity.uploadRate));
        for (auto& [id, torrent] : torrents)
        {
            CompletePriorities(torrent);
            PrepareFiles(torrent);
            FinishFiles(torrent);
            FinishDownload(torrent);
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
