#include "Engine/State.h"
#include <objbase.h>
#include <libtorrent/hex.hpp>
#include <libtorrent/read_resume_data.hpp>
#include <libtorrent/session_params.hpp>
#include <libtorrent/settings_pack.hpp>
#include <libtorrent/fingerprint.hpp>
#include <algorithm>
#include <stdexcept>
#include <utility>

namespace tt
{
namespace
{
constexpr auto statusInterval = std::chrono::seconds(1);
constexpr auto checkpointInterval = std::chrono::seconds(30);

constexpr std::pair<std::string_view, PauseReason> pauseReasons[] = {
    {"manual", PauseReason::Manual},
    {"schedule", PauseReason::Schedule},
    {"adapter", PauseReason::Adapter},
    {"connection_test", PauseReason::ConnectionTest}};

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
    directory(std::move(path)), store(wake), payload(wake), copies(wake), sources(wake), checks(wake),
    wake(std::move(wake)), language(settings.language)
{
    // Without a saved document the engine starts from the default settings.
    Document defaults;
    defaults.settings = settings;
    store.Run([this, saved = std::move(defaults)]() mutable
    {
        std::filesystem::create_directories(directory);
        auto file = directory / L"settings.json";
        if (std::filesystem::exists(file))
        {
            saved.Read(Json::parse(Store::Read(file)));
        }
        return std::move(saved);
    }, [this](std::expected<Document, StorageOutcome> saved)
    {
        if (!saved)
        {
            startupError = saved.error().detail;
            startup = Startup::Failed;
            log.Write("startup", "", "storage_failed");
            PublishSnapshot();
            return;
        }
        settings = saved->settings;
        language = settings.language;
        startup = Startup::Transfers;
        PublishSnapshot();
        std::vector<std::string> ids;
        for (auto const& [id, facts] : saved->torrents)
            ids.push_back(id);
        // Queued only now, so the settings completion drains alone and the
        // desktop shows the splash before Start loads the transfers on the
        // same thread.
        store.Run([this, ids = std::move(ids)]
        {
            Resumes resumes;
            for (auto const& id : ids)
            {
                try
                {
                    auto bytes = Store::Read(ResumeFile(id));
                    resumes[id] = lt::read_resume_data(lt::span<char const>(bytes.data(), bytes.size()));
                }
                catch (std::exception const&)
                {
                    // Start adds this torrent again from its saved hashes.
                }
            }
            return resumes;
        }, [this, document = std::move(*saved)](std::expected<Resumes, StorageOutcome> resumes)
        {
            if (!resumes)
            {
                startupError = resumes.error().detail;
                startup = Startup::Failed;
                log.Write("startup", "", "storage_failed");
                PublishSnapshot();
                return;
            }
            Start(document, *resumes);
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
    launchPaused = settings.startsPaused;
    watchedSources = saved.watchedSources;
    previousTransfers = saved.transfers;
    savedTransfers = saved.transfers;
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
    // ABI 100 leaves filter changes to us; retain libtorrent's initial class
    // assignments without reading them back during policy changes.
    peerClasses = session->get_peer_class_type_filter();
    if (wake)
    {
        session->set_alert_notify(wake);
    }
    RefreshPolicy();
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
    ContinueNames();
    queueOrder = saved.queueOrder;
    ApplyQueue();
    for (auto& [id, torrent] : torrents)
    {
        torrent.ApplyIntent();
    }
    startup = Startup::Ready;
    log.Write("startup", "", "ready");
    PublishSnapshot();
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
        if (!torrent.restore || Removing(torrent.status.info_hashes) || FindDuplicate(torrent.status.info_hashes, id))
        {
            continue;
        }
        Restore(id, torrent.facts, std::move(*torrent.restore));
        torrent.ApplyIntent();
        queuePending = true;
    }
}

std::filesystem::path Engine::State::ResumeFile(std::string const& id) const
{
    return directory / Wide(id + ".resume");
}

Json Engine::State::TransferTotals::ToJson() const
{
    return {{"downloaded", downloaded}, {"uploaded", uploaded}};
}

Engine::State::TransferTotals Engine::State::TotalTransfers() const
{
    auto maximum = (std::numeric_limits<std::int64_t>::max)();
    return {previousTransfers.downloaded + std::min(transferred.downloaded, maximum - previousTransfers.downloaded),
        previousTransfers.uploaded + std::min(transferred.uploaded, maximum - previousTransfers.uploaded)};
}

void Engine::State::SaveTransfers()
{
    if (transfersPending || TotalTransfers() == savedTransfers)
        return;
    transfersPending = true;
    if (!changes.Queue([this]
    {
        auto document = Saved();
        auto totals = document.transfers;
        changes.Commit([document = std::move(document)] { return document.ToJson(); }, [this, totals](Outcome outcome)
        {
            transfersPending = false;
            if (!outcome.error)
                savedTransfers = totals;
            else
                FailFinalSave(outcome.detail);
        });
    }))
    {
        transfersPending = false;
        log.Write("checkpoint", "", "storage_overloaded");
        FailFinalSave();
    }
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
        {"queue_order", queueOrder}, {"watched_sources", watchedSources}, {"transfers", transfers.ToJson()}};
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
    if (auto found = saved.find("transfers"); found != saved.end() && found->is_object())
    {
        transfers.downloaded = ReadSaved(*found, "downloaded", std::int64_t(0));
        transfers.uploaded = ReadSaved(*found, "uploaded", std::int64_t(0));
    }
    if (auto found = saved.find("watched_sources"); found != saved.end() && found->is_object())
        for (auto const& [source, stamp] : found->items())
            if (watchedSources.size() < torrentLimit && IsAbsolute(source) && stamp.is_string())
                watchedSources.emplace(source, stamp.get<std::string>());
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

// Choices change only after their commit succeeds; cumulative transfers also
// include the latest observed payload so a later settings save cannot erase it.
Engine::State::Document Engine::State::Saved() const
{
    Document document;
    document.settings = settings;
    document.transfers = TotalTransfers();
    document.watchedSources = watchedSources;
    for (auto const& [id, torrent] : torrents)
    {
        document.torrents.emplace(id, torrent.facts);
    }
    document.queueOrder = queueOrder;
    std::erase_if(document.queueOrder, [&document](auto const& id) { return !document.torrents.contains(id); });
    return document;
}

void Engine::State::CommitFacts(std::vector<std::string> const& ids,
    std::function<void(std::string const& id, Facts& facts)> edit,
    std::function<void(Outcome outcome, std::vector<std::string> const& changed)> done)
{
    auto document = Saved();
    std::vector<std::string> changed;
    for (auto const& id : ids)
    {
        auto& facts = document.torrents.at(id);
        edit(id, facts);
        if (facts.ToJson() != torrents.at(id).facts.ToJson())
        {
            changed.push_back(id);
        }
    }
    if (changed.empty())
    {
        done({}, changed);
        return;
    }
    changes.Commit([document = std::move(document)] { return document.ToJson(); },
        [this, changed, edit = std::move(edit), done = std::move(done)](Outcome outcome)
    {
        if (!outcome.error)
        {
            for (auto const& id : changed)
            {
                edit(id, torrents.at(id).facts);
            }
        }
        done(std::move(outcome), changed);
    });
}

std::optional<std::string> Engine::State::FindDuplicate(lt::info_hash_t const& hashes, std::string const& excluded) const
{
    auto values = Hashes(hashes);
    for (auto const& [id, torrent] : torrents)
    {
        if (id != excluded && Overlaps(values, torrent.Hashes()))
        {
            return id;
        }
    }
    return std::nullopt;
}

bool Engine::State::Overlaps(std::vector<std::string> const& hashes, std::vector<std::string> const& others)
{
    return std::any_of(hashes.begin(), hashes.end(),
        [&others](std::string const& hash) { return Contains(others, hash); });
}

tt::Activity Engine::State::Activity() const
{
    tt::Activity activity;
    auto paused = Paused();
    for (auto const& [id, torrent] : torrents)
    {
        ++activity.torrentCount;
        activity.downloadRate += torrent.status.download_payload_rate;
        activity.uploadRate += torrent.status.upload_payload_rate;
        activity.hasIncoming |= torrent.status.has_incoming;
        if (torrent.Diagnose())
        {
            ++activity.errorCount;
        }
        auto status = torrent.Classify(paused.has_value());
        // A torrent whose move waits transfers until the move starts and the
        // engine holds it.
        if (status == Status::Moving && !torrent.IsHeld(torrent.status.has_metadata))
        {
            status = torrent.ClassifyTransfer(paused.has_value());
        }
        switch (status)
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
    activity.downloading = activity.downloading && !IsShuttingDown();
    activity.seeding = activity.seeding && !IsShuttingDown();
    activity.paused = paused.has_value();
    activity.pausedByChoice = paused == PauseReason::Manual || paused == PauseReason::Schedule;
    if (IsAdapterMissing())
        activity.missingAdapter = settings.networkAdapter;
    for (auto const& [word, toast] : toastSwitches)
    {
        if (settings.*Settings::Switch(toast))
        {
            activity.toasts.insert(toast);
        }
    }
    activity.preventsSleep = settings.preventsSleep;
    activity.preventsSleepSeeding = settings.preventsSleepSeeding;
    activity.reportedPrograms = settings.reportedPrograms;
    activity.confirmsExit = settings.confirmsExit;
    activity.theme = settings.theme;
    return activity;
}

Json Engine::State::Snapshot()
{
    // The accepted order, because libtorrent's status reports a queue move
    // only after its next update, and the list would show the old order.
    std::map<std::string, int> queue;
    int position = 0;
    for (auto const& id : CurrentQueue())
    {
        queue[id] = position++;
    }
    auto paused = Paused();
    Json rows = Json::array();
    for (auto const& [id, torrent] : torrents)
    {
        auto queued = queue.find(id);
        rows.push_back(torrent.Row(paused.has_value(), queued == queue.end() ? -1 : queued->second,
            MoveProgress(torrent)));
    }
    auto current = settings.ToJson();
    current["language"] = language;
    auto activity = Activity();
    auto mode = CurrentLimits();
    auto caps = settings.Caps(mode);
    auto origin = !settings.scheduleEnabled ? "manual" : limitOverride ? "override" : "schedule";
    // Paused puts a pause that Resume all lifts first, so "adapter" means that
    // only the missing adapter pauses transfers and the window offers Settings
    // instead.
    auto pause = paused ? Word(pauseReasons, *paused) : std::string_view();
    return {{"session_id", sessionId}, {"sequence", ++snapshotSequence},
        {"torrents", std::move(rows)}, {"settings", std::move(current)},
        {"language_saved", language == settings.language},
        {"download_rate", activity.downloadRate}, {"upload_rate", activity.uploadRate},
        {"move_rate", move ? Json(move->rate) : Json()},
        {"check_rate", checkRate ? Json(*checkRate) : Json()},
        {"transfers", {{"session", transferred.ToJson()}, {"all_time", TotalTransfers().ToJson()}}},
        {"session_paused", paused.has_value()},
        {"connection_test", ConnectionTestSnapshot()},
        {"limits", {{"origin", origin}, {"mode", Settings::Name(mode)},
            {"download", caps.download}, {"upload", caps.upload}, {"pause", pause}}},
        {"missing_adapter", activity.missingAdapter.value_or("")},
        {"has_incoming", activity.hasIncoming},
        {"external_ipv4", IsAdapterMissing() ? "" : externalIpv4},
        {"external_ipv6", IsAdapterMissing() ? "" : externalIpv6},
        {"proxy", proxyOutcome ? Json(std::string(Name(*proxyOutcome))) : Json()},
        {"proxy_check", !requestedCheck ? Json() : Json{{"check_id", requestedCheck->checkId},
            {"outcome", requestedCheck->result ? Json(std::string(Name(requestedCheck->result->outcome))) : Json()},
            {"milliseconds", requestedCheck->result ? Json(requestedCheck->result->elapsed.count()) : Json()}}},
        {"shutting_down", IsShuttingDown()}, {"loading", IsLoading()}, {"storage_failed", startup == Startup::Failed},
        {"startup_error", startupError}};
}

Torrent* Engine::State::Find(lt::torrent_handle const& handle)
{
    auto found = handles.find(handle);
    return found == handles.end() ? nullptr : found->second;
}

Torrent& Engine::State::Working(std::string const& id)
{
    return const_cast<Torrent&>(std::as_const(*this).Working(id));
}

Torrent const& Engine::State::Working(std::string const& id) const
{
    if (auto torrent = FindWorking(id))
        return *torrent;
    throw std::out_of_range("No torrent " + id);
}

Torrent* Engine::State::FindWorking(std::string const& id)
{
    return const_cast<Torrent*>(std::as_const(*this).FindWorking(id));
}

Torrent const* Engine::State::FindWorking(std::string const& id) const
{
    if (auto found = torrents.find(id); found != torrents.end())
        return &found->second;
    auto removal = removed.find(id);
    return removal == removed.end() ? nullptr : &removal->second.torrent;
}

Torrent* Engine::State::FindWorking(lt::torrent_handle const& handle)
{
    if (auto torrent = Find(handle))
        return torrent;
    for (auto& [id, removal] : removed)
    {
        if (removal.torrent.handle == handle)
            return &removal.torrent;
    }
    return nullptr;
}

Torrent& Engine::State::Install(std::string const& id, lt::torrent_handle handle, Facts facts,
    lt::add_torrent_params const& params)
{
    ApplyPolicy(handle);
    auto& torrent = torrents.insert_or_assign(id, Torrent{id, handle, std::move(facts)}).first->second;
    torrent.history.Configure(settings.recentInterval, settings.historyInterval);
    torrent.comment = params.comment;
    torrent.creator = params.created_by;
    torrent.created = params.creation_date;
    if (params.ti)
    {
        torrent.names = FileNames{params.ti, {}};
        torrent.names->mappings.import_filenames(params.ti->layout(), params.renamed_files);
    }
    std::uint8_t tier = 0;
    auto nextTier = params.tracker_tiers.begin();
    for (auto const& url : params.trackers)
    {
        if (url.empty())
            continue;
        if (nextTier != params.tracker_tiers.end())
            tier = static_cast<std::uint8_t>(*nextTier++);
        if (std::any_of(torrent.sourceTrackers.begin(), torrent.sourceTrackers.end(),
            [&url](auto const& tracker) { return tracker.url == url; }))
            continue;
        torrent.sourceTrackers.emplace_back(url);
        torrent.sourceTrackers.back().tier = tier;
    }
    torrent.status.handle = handle;
    torrent.status.name = params.ti ? params.ti->name() : params.name;
    torrent.status.info_hashes = params.ti ? params.ti->info_hashes() : params.info_hashes;
    torrent.status.has_metadata = bool(params.ti);
    torrent.status.state = params.ti ? lt::torrent_status::checking_resume_data : lt::torrent_status::downloading_metadata;
    torrent.status.flags = params.flags;
    torrent.status.queue_position = lt::queue_position_t{-1};
    torrent.status.all_time_download = params.total_downloaded;
    torrent.status.all_time_upload = params.total_uploaded;
    torrent.namePhase = NamePhase::Pending;
    torrent.savedUploaded = params.total_uploaded;
    torrent.filesPending = true;
    handles.emplace(handle, &torrent);
    statusPending = true;
    return torrent;
}

void Engine::State::Notify(ToastKind kind, Torrent const& torrent, std::string detail, std::string code)
{
    Notify(kind, torrent.Name(), std::move(detail), torrent.torrentId, std::move(code));
}

void Engine::State::Notify(ToastKind kind, std::string name, std::string detail, std::string torrentId,
    std::string code, std::string folder)
{
    log.Write("notification", torrentId, ToString(kind));
    if (toasts.size() == Toast::limit)
    {
        toasts.erase(toasts.begin());
    }
    toasts.push_back(
        {.kind = kind, .name = std::move(name), .detail = std::move(detail), .code = std::move(code),
            .torrentId = std::move(torrentId), .folder = std::move(folder)});
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
        if (!torrents.contains(id))
        {
            return;
        }
        CommitFacts({id}, [hashes](std::string const&, Facts& facts) { facts.hashes = hashes; },
            [this, id](Outcome outcome, auto const&)
        {
            auto& torrent = torrents.at(id);
            if (!outcome.error)
            {
                torrent.hashError.reset();
            }
            else
            {
                torrent.hashError = Problem::From(outcome, ProblemKind::StorageFailed);
                FailFinalSave(outcome.detail);
            }
        });
    }))
    {
        torrent.hashError = Problem{ProblemKind::StorageOverloaded};
        FailFinalSave();
    }
}

Engine::State::~State()
{
    sources.Abandon();
    {
        std::lock_guard lock(checkGate);
        checkStop.request_stop();
    }
    if (connectionTest)
        connectionTest->stop.request_stop();
    checks.Abandon();
}

void Engine::State::Tick()
{
    store.Drain();
    payload.Drain();
    copies.Drain();
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
        if (reading && !alerts.empty())
            ContinueReading(std::exchange(reading->changed, false));
        ContinueRename();
        ContinueDeletion();
        RemoveDeferred();
        if (!IsShuttingDown())
        {
            Maintain();
        }
        QueryCompletions();
        QueryFiles();
        if (queuePending)
            ApplyQueue();
        if (statusPending)
            RequestStatus();
    }
    if (IsShuttingDown())
    {
        ContinueShutdown();
    }
    log.Flush();
}

void Engine::State::SampleChecks(std::chrono::steady_clock::duration elapsed)
{
    // While libtorrent checks the files, a torrent's progress is the share of
    // its bytes checked. A check counts from its second sample, so a torrent
    // that starts checking does not add the bytes it checked before. A paused
    // torrent keeps the checking state but checks nothing: it is stopped,
    // waits for a check slot, or every transfer is paused.
    std::optional<double> rate;
    auto seconds = std::chrono::duration<double>(elapsed).count();
    bool sessionPaused = Paused().has_value();
    for (auto& [id, torrent] : torrents)
    {
        if (sessionPaused || torrent.status.state != lt::torrent_status::checking_files ||
            (torrent.status.flags & lt::torrent_flags::paused))
        {
            torrent.checked.reset();
            continue;
        }
        auto checked = static_cast<std::uint64_t>(torrent.status.total) *
            static_cast<std::uint64_t>(torrent.status.progress_ppm) / 1'000'000;
        auto previous = std::exchange(torrent.checked, checked);
        auto bytes = previous && checked > *previous ? checked - *previous : 0;
        rate = rate.value_or(0) + double(bytes) / seconds;
    }
    checkRate = rate;
}

void Engine::State::Maintain()
{
    auto now = std::chrono::steady_clock::now();
    if (now - statusAt >= statusInterval)
    {
        SampleMove(now - statusAt);
        SampleChecks(now - statusAt);
        statusAt = now;
        session->post_session_stats();
        if (SuspendsForConnectionTest() ||
            (connectionTest && connectionTest->phase == ConnectionPhase::Restoring))
            snapshotPending = true;
        MaintainConnectionTest();
        RestorePending();
        WatchFolder();
        RefreshPolicy();
        LimitSeeds();
        auto time = std::chrono::system_clock::to_time_t(std::chrono::system_clock::now());
        auto activity = Activity();
        history.Sample(time, double(activity.downloadRate), double(activity.uploadRate));
        ContinueNames();
        ResumeMove();
        MoveWaiting();
        for (auto& [id, torrent] : torrents)
        {
            torrent.history.Sample(time, double(torrent.status.download_payload_rate),
                double(torrent.status.upload_payload_rate));
            QueryPriorities(torrent);
            ApplyWaiting(torrent);
            FinishFiles(torrent);
            FinishDownload(torrent);
        }
        RequestStatus(false);
    }
    if (now - checkpointAt >= checkpointInterval)
    {
        checkpointAt = now;
        SaveTransfers();
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

void Engine::State::PublishSnapshot()
{
    if (snapshotSubscribers.empty())
        return;
    auto message = Success(Snapshot());
    message["type"] = "snapshot";
    for (auto const& [connectionId, send] : snapshotSubscribers)
        send(message);
}

void Engine::State::RequestStatus(bool immediate)
{
    snapshotPending |= immediate || statusPending;
    if (immediate)
        // An outstanding answer may predate this command, so query once more.
        statusPending = true;
    if (!session || statusOutstanding)
        return;
    statusOutstanding = true;
    statusPending = false;
    session->post_torrent_updates(routineQuery);
}
}
