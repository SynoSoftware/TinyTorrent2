#pragma once

#include "Changes.h"
#include "Engine.h"
#include "Log.h"
#include "SpeedHistory.h"
#include "Store.h"
#include "Torrent.h"
#include <libtorrent/add_torrent_params.hpp>
#include <libtorrent/alert_types.hpp>
#include <libtorrent/session.hpp>
#include <chrono>
#include <filesystem>
#include <functional>
#include <list>
#include <map>
#include <memory>
#include <optional>
#include <stop_token>
#include <string>
#include <vector>

namespace tt
{
// The engine's state and behavior. Engine.cpp holds the public API, and each
// source file in src/Engine holds one responsibility.
class Engine::State
{
public:
    static constexpr std::size_t torrentLimit = 10'000;

    struct Preview
    {
        std::string previewId;
        std::string connectionId;
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
        struct Choices
        {
            std::string destination;
            Intent intent = Intent::Resumed;
            std::vector<lt::download_priority_t> priorities;
            bool sequential = false;
            bool firstLast = false;
            bool queueTop = false;
        };

        std::string torrentId;
        lt::add_torrent_params params;
        Facts facts;
        bool queueTop = false;
        std::function<void(Outcome, Added)> completion;
        lt::torrent_handle handle;
        AdditionPhase phase = AdditionPhase::Adding;
        std::set<lt::file_index_t> renaming;
    };

    // The person's settings, saved in the document. A setting that the
    // document lacks or holds incorrectly keeps its default.
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

        struct Proxy
        {
            ProxyType type = ProxyType::None;
            std::string host;
            int port = 0;
            std::string username;
            std::string password;

            bool operator==(Proxy const&) const = default;
        };

        std::string destination;
        std::string language;
        std::string incompleteFolder;
        bool usesIncompleteFolder = false;
        bool appendsSuffix = true;
        bool confirmsExit = true;
        bool showsExternalIp = false;
        int diskBufferMib = 100;
        int checkingMib = 4;
        int hashingThreads = 1;
        int fileLimit = 40;
        // Only the window and the splash act on the theme.
        std::string theme = "system";
        bool mapsPorts = true;
        int listenPort = 6881;
        std::string networkAdapter;
        int activeDownloads = 3;
        int activeSeeds = 5;
        int connections = 200;
        Encryption encryption = Encryption::Preferred;
        Proxy proxy;
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
        // The choice while no schedule applies.
        LimitMode limitMode = LimitMode::None;
        bool notificationsEnabled = false;
        bool notifiesProblems = true;
        bool notifiesAdded = false;
        bool preventsSleep = true;
        bool preventsSleepSeeding = false;
        bool backgroundNoticeShown = false;
        // The missing programs that torrent handlers start, as last reported
        // to the person, so each one is reported once.
        std::vector<std::string> reportedPrograms;

        // The settings for the settings reply and the snapshot.
        Json ToJson() const;
        // The settings for settings.json, which holds the proxy password
        // encrypted; Read decrypts it.
        Json ToFile() const;
        void Read(Json const& saved);
        // A copy with a settings command's changes applied, or nothing when a
        // change names an unknown setting, holds an invalid value or leaves
        // the proxy without its address.
        std::optional<Settings> With(Json const& changes) const;
        // Where a new torrent saves when the person chooses `destination`:
        // the incomplete-download folder while one is in use, so the torrent
        // moves to `destination` when it finishes.
        std::string SavePath(std::string const& destination) const;
        Limits Caps(LimitMode mode) const;
        // The mode's name in settings.json, the settings command and the snapshot.
        static char const* Name(LimitMode mode);
        static std::optional<LimitMode> Named(Json const& value);
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

    struct ProxyCheck
    {
        ProxyOutcome outcome = ProxyOutcome::TimedOut;
        std::chrono::milliseconds elapsed{};
    };

    // A check that check_proxy started; its result is nothing while it runs.
    struct RequestedCheck
    {
        std::string checkId;
        std::optional<ProxyCheck> result;
    };

    // A command's work on a list of torrents that all exist.
    using Action = std::function<void(std::vector<std::string> const& ids, Reply reply)>;

    // Declared before the members that they initialize, because IntelliSense
    // cannot find them otherwise.
    static std::string NewId();
    static Settings Defaults();

    std::filesystem::path directory;
    Store store;
    Store payload;
    // Reads preview sources, which can block on a slow share.
    Store sources;
    // Checks proxies, which can wait for one that does not answer.
    Store checks;
    // Ends a running proxy check when the engine closes.
    std::stop_source checkStop;
    Log log{store, directory};
    Changes changes{store, directory / L"settings.json", log};
    std::function<void()> wake;
    std::unique_ptr<lt::session> session;
    std::string sessionId = NewId();
    std::string externalIpv4;
    std::string externalIpv6;
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
    bool shuttingDown = false;
    ShutdownPhase shutdownPhase = ShutdownPhase::Draining;
    std::vector<lt::torrent_handle> pausing;
    // When the current shutdown phase started to wait.
    std::optional<std::chrono::steady_clock::time_point> phaseStarted;
    // Set during shutdown when the final save cannot complete: its cause, or
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
    std::optional<LimitMode> limitOverride;
    bool adapterMissing = false;
    std::string appliedListen;
    std::optional<Settings::Proxy> appliedProxy;
    std::optional<Encryption> appliedEncryption;
    // The check of the proxy in use; nothing until it ends.
    std::optional<ProxyOutcome> proxyOutcome;
    // The check that check_proxy started last. The snapshot reports it,
    // because a check can take longer than the client waits for a reply.
    std::optional<RequestedCheck> requestedCheck;
    std::optional<bool> appliedPause;
    std::optional<LimitMode> appliedLimits;
    std::vector<std::string> limitingSeeds;

    // What a file operation on the selected torrents reaches. Both path lists
    // are sorted by PathBefore.
    struct Scope
    {
        // Outside torrents that share files with the selection, directly or
        // through another of them.
        std::vector<std::string> shared;
        std::vector<std::filesystem::path> files;
        std::vector<std::filesystem::path> holds;
        // The selection's files that an outside torrent also uses.
        std::vector<std::filesystem::path> kept;
    };
    struct Move
    {
        std::vector<std::string> ids;
        std::string destination;
        bool usesExisting = false;
        std::size_t current = 0;
        std::vector<lt::torrent_handle> waiting;
        std::vector<std::filesystem::path> holds;
        std::vector<std::filesystem::path> moved;
        MovePhase phase = MovePhase::Preparing;
    };
    struct Deletion
    {
        std::vector<lt::torrent_handle> waiting;
        std::vector<std::filesystem::path> holds;
        std::vector<std::filesystem::path> files;
        std::vector<std::filesystem::path> roots;
        std::string names;
        DeletionPhase phase = DeletionPhase::Saving;
    };
    std::optional<Move> move;
    std::list<Deletion> deletions;
    struct Rename
    {
        struct Owner
        {
            std::string torrentId;
            lt::file_index_t index;
            std::string name;
        };
        std::vector<Owner> owners;
        std::vector<lt::torrent_handle> waiting;
        std::size_t current = 0;
        std::filesystem::path source;
        std::filesystem::path target;
        RenamePhase phase = RenamePhase::Waiting;
    };
    std::optional<Rename> rename;

    using Resumes = std::map<std::string, lt::add_torrent_params>;

    State(std::filesystem::path path, std::function<void()> wake);
    ~State();
    static bool Contains(std::vector<std::string> const& values, std::string const& value);
    void Start(Document const& saved, Resumes& resumes);
    void Restore(std::string const& id, Facts facts, lt::add_torrent_params params);
    void RestorePending();
    std::filesystem::path ResumeFile(std::string const& id) const;
    Document Saved() const;
    void Tick();
    void Maintain();
    Torrent* Find(lt::torrent_handle const& handle);
    Torrent& Install(std::string const& id, lt::torrent_handle handle, Facts facts,
        lt::add_torrent_params const& params);
    void Notify(NoticeKind kind, Torrent const& torrent, std::string detail = {}, std::string code = {});
    void Notify(NoticeKind kind, std::string name, std::string detail, std::string torrentId = {},
        std::string code = {});
    tt::Activity Activity() const;
    Json Snapshot() const;
    std::string FindDuplicate(lt::info_hash_t const& hashes, std::string const& excluded = {}) const;
    static bool Overlaps(std::vector<std::string> const& hashes, std::vector<std::string> const& others);
    void RecordHashes(Torrent& torrent);
    void RecordHashes(Torrent& torrent, lt::info_hash_t const& hashes);

    void RefreshPolicy(bool configure = false);
    ScheduleMode ScheduledMode() const;
    bool IsPaused() const;
    // Paused by the person or their schedule, which Resume all lifts; a missing
    // adapter pauses transfers too, but Resume all cannot lift that.
    bool IsPausedByChoice() const;
    LimitMode CurrentLimits() const;
    void LimitSeeds();
    bool ReachedSeedLimit(Torrent const& torrent) const;
    void Configure(Json const& choices, Reply reply);
    // Connects to the proxy and signs in, without changing the session.
    // `completion` receives nothing when the check cannot run.
    void CheckProxy(Settings::Proxy proxy, std::function<void(std::optional<ProxyCheck>)> completion);
    // The outcome's name in the snapshot.
    static std::string_view Name(ProxyOutcome outcome);
    static bool IsAbsolute(std::string const& path);
    void PauseSession(bool paused, std::function<void(Outcome)> completion);
    void RecordBackgroundNotice(std::function<void(Outcome)> completion);
    void RecordPrograms(std::vector<std::string> programs, std::function<void(Outcome)> completion);

    void UpdatePreview(Preview& preview);
    void Merge(Preview& existing, Preview const& source);
    bool CanMerge(Preview const& preview) const;
    Json Describe(Preview const& preview, std::string const& destination) const;
    void Inspect(std::string source, std::string connectionId, std::function<void(Outcome, Preview*)> completion);
    Preview* FindPreview(std::string const& previewId, std::string const& connectionId);
    void Discard(std::function<bool(Preview const&)> const& matches);
    void Disconnect(std::string const& connectionId);
    std::vector<std::string> SharedFiles(std::shared_ptr<lt::torrent_info const> const& metadata,
        std::string const& destination) const;
    static std::vector<std::string> Missing(std::vector<std::string> const& urls,
        std::vector<std::string> known);
    void On(lt::metadata_failed_alert const& alert);

    void Add(Preview& preview, Addition::Choices choices, std::function<void(Outcome, Added)> completion);
    void AddSource(std::string source, std::function<void(Outcome, Added)> completion);
    static void Guard(lt::add_torrent_params& params);
    static std::optional<std::vector<lt::download_priority_t>> Priorities(
        std::vector<lt::download_priority_t> chosen, std::shared_ptr<lt::torrent_info const> const& metadata);
    static bool IsChoice(lt::download_priority_t priority);
    void SaveAddition(std::string id, lt::torrent_handle handle);
    void PrepareAddition(std::string const& id, lt::torrent_handle handle);
    void CommitAddition(std::string const& id);
    void Abandon(std::string id, Outcome outcome, Added added = {});
    std::string MovingAddition(lt::torrent_handle const& handle) const;
    void RecoverAdditions();
    void On(lt::add_torrent_alert const& alert);
    void On(lt::storage_moved_alert const& alert);
    void On(lt::storage_moved_failed_alert const& alert);

    std::optional<ErrorCode> Refusal() const;
    void Execute(Json const& request, std::string const& connectionId, Reply reply);
    void MergeTrackers(Preview& preview, std::string const& torrentId, Reply reply);
    void Act(std::vector<std::string> ids, Reply reply, Action action, BusyFiles busy = BusyFiles::Refused);
    void Verify(std::vector<std::string> const& ids, Reply reply);
    void Remove(std::vector<std::string> const& ids, Reply reply, bool deleteData = false);
    std::list<Deletion>::iterator PrepareDeletion(std::vector<std::string> const& ids);
    void RemoveHandles(std::vector<std::string> const& ids, std::list<Deletion>::iterator deletion);
    bool CanRemove(Torrent const& torrent) const;
    void RemoveDeferred();
    static std::filesystem::path FullPath(std::filesystem::path const& path);
    static bool PathBefore(std::filesystem::path const& left, std::filesystem::path const& right);
    static bool SamePath(std::filesystem::path const& left, std::filesystem::path const& right);
    static bool SameFolder(std::string const& left, std::string const& right);
    bool FilesBusy() const;
    static std::vector<std::filesystem::path> FilePaths(lt::torrent_info const& metadata,
        std::string const& folder);
    std::vector<std::filesystem::path> FilePaths(Torrent const& torrent,
        std::string const& destination = {}, bool logical = true) const;
    Scope FileScope(std::vector<std::string> const& ids) const;
    Json Describe(std::vector<std::string> const& ids, Scope const& scope) const;
    Outcome FilesReady(std::vector<std::string> const& ids) const;
    void StartMove(std::vector<std::string> const& ids, std::string const& destination,
        bool useExisting, std::function<void(Outcome)> completion);
    void ContinueMove();
    void FinishMove(lt::torrent_handle const& handle, std::optional<Problem> problem);
    void ContinueDeletion();
    void Delete(std::list<Deletion>::iterator deletion);
    void On(lt::torrent_deleted_alert const& alert);
    void On(lt::torrent_delete_failed_alert const& alert);
    void RecoverFiles();
    void PrepareFiles(Torrent& torrent);
    static void PrepareNames(lt::add_torrent_params& params, bool appendsSuffix);
    // Returns whether any rename was requested.
    static bool ApplyNames(lt::torrent_handle const& handle, lt::add_torrent_params const& prepared,
        std::set<lt::file_index_t>& pending);
    void ContinueNames();
    void FinishNames(Torrent& torrent);
    void CompleteFiles(Torrent& torrent);
    void FinishFiles(Torrent& torrent);
    void FinishDownload(Torrent& torrent);
    void ContinueRename();
    void EndRename();
    void On(lt::file_completed_alert const& alert);
    void On(lt::file_renamed_alert const& alert);
    void On(lt::file_rename_failed_alert const& alert);
    std::vector<std::string> Holders(std::shared_ptr<lt::torrent_info const> const& metadata,
        std::string const& destination) const;
    void SetIntent(std::vector<std::string> const& ids, Intent intent, Reply reply);
    // Saves `change` to each listed torrent's facts, then applies them.
    void ChangeFacts(std::vector<std::string> const& ids, std::function<void(Facts&)> const& change,
        Reply reply);
    void Edit(std::string const& id, Json const& choices, Reply reply);
    void CompletePriorities(Torrent& torrent);
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
    void ContinueShutdown();
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

private:
    SpeedHistory history;

    void CommitEdit(std::string const& id,
        std::map<int, lt::download_priority_t> const& priorities,
        std::optional<std::vector<lt::announce_entry>> const& trackers, Reply reply);
};
}
