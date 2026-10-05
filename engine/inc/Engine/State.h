#pragma once

#include "Changes.h"
#include "Engine.h"
#include "Log.h"
#include "Store.h"
#include "Torrent.h"
#include <libtorrent/add_torrent_params.hpp>
#include <libtorrent/alert_types.hpp>
#include <libtorrent/session.hpp>
#include <chrono>
#include <deque>
#include <filesystem>
#include <functional>
#include <map>
#include <memory>
#include <optional>
#include <string>
#include <vector>

namespace tt
{
// The engine's state and behavior. Engine.cpp holds the public API, and each
// source file in src/Engine holds one responsibility.
class Engine::State
{
public:
    static constexpr std::size_t targetLimit = 10'000;

    struct Preview
    {
        std::string identity;
        std::string connection;
        lt::add_torrent_params params;
        lt::torrent_handle handle;
        std::string error;
        bool cancelled = false;

        lt::info_hash_t InfoHashes() const
        {
            return params.ti ? params.ti->info_hashes() : params.info_hashes;
        }
    };

    struct Addition
    {
        std::string identity;
        lt::add_torrent_params params;
        Facts facts;
        std::function<void(Outcome, Added)> done;
        lt::torrent_handle handle;
        AdditionPhase phase = AdditionPhase::Adding;
    };

    // The person's settings, saved in the document. A document that lacks a
    // setting keeps its default.
    struct Settings
    {
        struct Limits
        {
            int download = 0;
            int upload = 0;
        };

        struct Period
        {
            std::vector<int> days;
            int start = 0;
            int end = 0;
            ScheduleMode mode = ScheduleMode::Alternative;

            bool Contains(int day, int minute) const;
            Json ToJson() const;
            static std::optional<Period> Read(Json const& value);
        };

        std::string destination;
        std::string language;
        // Only the window and the splash act on the theme.
        std::string theme = "system";
        bool portMapping = true;
        int listenPort = 6881;
        std::string networkInterface;
        int activeDownloads = 3;
        int activeSeeds = 5;
        int connections = 200;
        double ratio = 0;
        int seedingMinutes = 0;
        bool checksUpdates = true;
        bool scheduleEnabled = false;
        std::vector<Period> schedule;
        bool showsAdd = true;
        bool showsSplash = true;
        bool startsInTray = false;
        bool allPaused = false;
        Limits limits;
        Limits alternative{10 * 1024, 10 * 1024};
        bool usesAlternative = false;
        bool notificationsEnabled = true;
        bool preventSleep = true;
        bool preventSleepSeeding = false;
        bool backgroundNoticeShown = false;

        Json ToJson() const;
        void Read(Json const& saved);
        // A copy with a settings command's changes applied, or nothing when a
        // change names an unknown setting or holds an invalid value.
        std::optional<Settings> With(Json const& changes) const;
    };

    // The saved document. A change edits a copy of the saved one and commits
    // it.
    struct Document
    {
        static constexpr int format = 1;

        Settings settings;
        std::map<std::string, Facts> torrents;
        std::vector<std::string> queueOrder;

        Json ToJson() const;
        void Read(Json const& saved);
    };

    // A command's work on a list of torrents that all exist.
    using Action = std::function<void(std::vector<std::string> const& ids, Reply reply)>;

    std::filesystem::path directory;
    Store store;
    Store payload;
    // Reads preview sources, which can block on a slow share.
    Store sources;
    Log diagnostics{store, directory};
    Changes changes{store, directory / L"settings.json", diagnostics};
    std::function<void()> wake;
    std::unique_ptr<lt::session> session;
    std::string sessionId = Identity();
    Settings settings = Defaults();
    std::vector<std::string> queueOrder;
    std::string language;
    std::map<std::string, Torrent> torrents;
    std::map<lt::torrent_handle, Torrent*> handles;
    std::vector<Notice> notices;
    std::map<std::string, Preview> previews;
    std::map<std::string, Addition> additions;
    std::vector<std::shared_ptr<Preview>> parsing;
    Startup startup = Startup::Settings;
    bool stopping = false;
    ExitStep exitStep = ExitStep::Draining;
    std::vector<lt::torrent_handle> pausing;
    // When the current exit step started to wait.
    std::optional<std::chrono::steady_clock::time_point> stepStarted;
    // Set during Exit when the final save cannot complete: its cause, or
    // empty when the cause is unknown.
    std::optional<std::string> saveFailure;
    std::string startupError;
    std::function<void(std::optional<std::string> failure)> shutdown;
    std::chrono::steady_clock::time_point checkpointAt = std::chrono::steady_clock::now();
    // The torrent CheckpointUnsaved chose last; the next choice starts after it.
    std::string checkpointCursor;
    std::chrono::steady_clock::time_point statusAt{};
    std::optional<ScheduleMode> scheduledMode;
    bool bypassesScheduledPause = false;
    std::optional<bool> alternativeOverride;
    bool interfaceMissing = false;
    std::string appliedListen;
    std::optional<bool> appliedPause;
    std::optional<bool> appliedAlternative;
    std::vector<std::string> limitingSeeds;
    struct SpeedSample
    {
        std::int64_t time = 0;
        double download = 0;
        double upload = 0;
    };
    std::deque<SpeedSample> seconds;
    std::deque<SpeedSample> minutes;
    SpeedSample minute;
    int minuteCount = 0;

    // What a file operation on the selected torrents reaches. Both path lists
    // are sorted by PathBefore.
    struct Scope
    {
        // Outside torrents that share files with the selection, directly or
        // through another of them.
        std::vector<std::string> shared;
        std::vector<std::filesystem::path> files;
        // The selection's files that an outside torrent also uses.
        std::vector<std::filesystem::path> kept;
    };
    struct Relocation
    {
        std::vector<std::string> ids;
        std::string destination;
        bool usesExisting = false;
        std::size_t current = 0;
        std::vector<lt::torrent_handle> waiting;
        std::vector<std::filesystem::path> holds;
        std::vector<std::filesystem::path> moved;
        RelocationPhase phase = RelocationPhase::Preparing;
    };
    struct Deletion
    {
        std::vector<lt::torrent_handle> waiting;
        std::vector<std::filesystem::path> holds;
        std::vector<std::filesystem::path> files;
        std::vector<std::filesystem::path> roots;
        std::string names;
        DeletionPhase phase = DeletionPhase::Waiting;
    };
    std::optional<Relocation> relocation;
    std::optional<Deletion> deletion;

    using Resumes = std::map<std::string, lt::add_torrent_params>;

    State(std::filesystem::path path, std::function<void()> notification);
    ~State();
    static std::string Identity();
    static bool Contains(std::vector<std::string> const& values, std::string const& value);
    void Start(Document const& saved, Resumes& resumes);
    std::filesystem::path ResumeFile(std::string const& id) const;
    Document Saved() const;
    void Tick();
    void Maintain();
    Torrent* Find(lt::torrent_handle const& handle);
    Torrent& Install(std::string const& id, lt::torrent_handle handle, Facts facts,
        lt::add_torrent_params const& params);
    void Notify(NoticeKind kind, Torrent const& torrent, std::string detail = {});
    void Notify(NoticeKind kind, std::string name, std::string detail, std::string id = {});
    tt::Activity Activity() const;
    Json Snapshot() const;
    std::string Duplicate(lt::info_hash_t const& hashes, std::string const& excluded = {}) const;
    static bool Overlaps(std::vector<std::string> const& hashes, std::vector<std::string> const& others);
    void RecordHashes(Torrent& torrent);
    void RecordHashes(Torrent& torrent, lt::info_hash_t const& hashes);

    static Settings Defaults();
    void RefreshPolicy(bool configure = false);
    ScheduleMode ScheduledMode() const;
    bool IsPaused() const;
    bool UsesAlternative() const;
    void LimitSeeds();
    bool ReachedSeedLimit(Torrent const& torrent) const;
    void Configure(Json const& choices, Reply reply);
    static bool IsAbsolute(std::string const& path);
    void PauseSession(bool paused, std::function<void(Outcome)> done);
    void RecordBackgroundNotice(std::function<void(Outcome)> done);

    void UpdatePreview(Preview& preview);
    void Merge(Preview& existing, Preview const& source);
    bool CanMerge(Preview const& preview) const;
    Json Describe(Preview const& preview, std::string const& destination) const;
    void Inspect(std::string source, std::string connection, std::function<void(Outcome, Preview*)> done);
    Preview* FindPreview(std::string const& id, std::string const& connection);
    void Discard(std::function<bool(Preview const&)> const& matches);
    void Disconnect(std::string const& connection);
    std::vector<std::string> SharedFiles(std::shared_ptr<lt::torrent_info const> const& metadata,
        std::string const& destination) const;
    static std::vector<std::string> Missing(std::vector<std::string> const& urls,
        std::vector<std::string> known);
    void On(lt::metadata_failed_alert const& alert);

    void Add(Preview& preview, std::string const& destination,
        std::vector<lt::download_priority_t> priorities, bool paused, std::function<void(Outcome, Added)> done);
    void AddSource(std::string source, std::function<void(Outcome, Added)> done);
    static void Guard(lt::add_torrent_params& params);
    static std::optional<std::vector<lt::download_priority_t>> Priorities(
        std::vector<lt::download_priority_t> chosen, std::shared_ptr<lt::torrent_info const> const& metadata);
    static bool IsChoice(lt::download_priority_t priority);
    void SaveAddition(std::string id, lt::torrent_handle handle);
    void CommitAddition(std::string const& id);
    void Abandon(std::string id, Outcome outcome, Added added = {});
    std::string MovingAddition(lt::torrent_handle const& handle) const;
    void RecoverAdditions();
    void On(lt::add_torrent_alert const& alert);
    void On(lt::storage_moved_alert const& alert);
    void On(lt::storage_moved_failed_alert const& alert);

    std::optional<ErrorCode> Refusal() const;
    void Execute(Json const& request, std::string const& connection, Reply reply);
    void MergeTrackers(Preview& preview, std::string const& id, Reply reply);
    void Act(std::vector<std::string> ids, Reply reply, Action action);
    void Verify(std::vector<std::string> const& ids, Reply reply);
    void Remove(std::vector<std::string> const& ids, Reply reply, bool deleteData = false);
    static std::filesystem::path FullPath(std::filesystem::path const& path);
    static bool PathBefore(std::filesystem::path const& left, std::filesystem::path const& right);
    static bool SamePath(std::filesystem::path const& left, std::filesystem::path const& right);
    static bool SameFolder(std::string const& left, std::string const& right);
    bool FilesBusy() const;
    static std::vector<std::filesystem::path> FilePaths(lt::torrent_info const& metadata,
        std::string const& folder);
    std::vector<std::filesystem::path> FilePaths(Torrent const& torrent,
        std::string const& destination = {}) const;
    Scope FileScope(std::vector<std::string> const& ids) const;
    Json Describe(std::vector<std::string> const& ids, Scope const& scope) const;
    bool FilesReady(std::vector<std::string> const& ids, Reply const& reply) const;
    void Move(std::vector<std::string> const& ids, std::string const& destination,
        bool useExisting, Reply reply);
    void ContinueMove();
    void FinishMove(lt::torrent_handle const& handle, std::optional<Problem> problem);
    void ContinueDeletion();
    void On(lt::torrent_deleted_alert const& alert);
    void On(lt::torrent_delete_failed_alert const& alert);
    void RecoverFiles();
    bool HoldsFiles(std::shared_ptr<lt::torrent_info const> const& metadata,
        std::string const& destination) const;
    void SetIntent(std::vector<std::string> const& ids, Intent intent, Reply reply);
    void Edit(std::string const& id, Json const& choices, Reply reply);
    void CompletePriorities(Torrent& torrent);
    void SampleHistory();
    Json History(bool day) const;

    static bool IsQueued(lt::queue_position_t position);
    std::vector<std::string> CurrentQueue() const;
    void ApplyQueue();
    void Queue(std::vector<std::string> const& ids, QueueMove move, std::string const& before, Reply reply);
    static std::vector<std::string> Reorder(std::vector<std::string> order,
        std::vector<std::string> const& ids, QueueMove move, std::string const& before);

    void WriteResume(std::string const& id, lt::add_torrent_params params,
        std::function<void(StorageOutcome)> completion);
    void CheckpointUnsaved();
    void SaveCheckpoint(Torrent& torrent, lt::add_torrent_params params);
    void AwaitCompletion(Torrent& torrent);
    void FailCheckpoint(Torrent& torrent, Problem problem);
    void On(lt::save_resume_data_alert const& alert);
    void On(lt::save_resume_data_failed_alert const& alert);

    void Shutdown(std::function<void(std::optional<std::string> failure)> completion);
    void Stop();
    void Finish();

    void Handle(lt::alert* alert);
    void On(lt::state_update_alert const& alert);
    void On(lt::torrent_finished_alert const& alert);
    void On(lt::cache_flushed_alert const& alert);
    void On(lt::torrent_conflict_alert const& alert);
    void On(lt::metadata_received_alert const& alert);
    void On(lt::torrent_error_alert const& alert);
    void On(lt::file_error_alert const& alert);
    void On(lt::alerts_dropped_alert const&);
};
}
