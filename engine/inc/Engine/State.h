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
#include <atomic>
#include <filesystem>
#include <functional>
#include <list>
#include <map>
#include <memory>
#include <mutex>
#include <optional>
#include <set>
#include <stop_token>
#include <string>
#include <vector>

namespace tt
{
struct NetworkAdapter
{
    unsigned ipv4Index = 0;
    unsigned ipv6Index = 0;
    std::vector<std::string> addresses;
};

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
            std::string watchSource;
            std::string watchStamp;
        };

        std::string torrentId;
        lt::add_torrent_params params;
        Facts facts;
        bool queueTop = false;
        std::string watchSource;
        std::string watchStamp;
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
        std::string lastFolder;
        bool startsDownload = true;
        bool queueTop = false;
        bool preallocates = false;
        bool usesLastFolder = false;
        bool startsPaused = false;
        bool raisesAdd = true;
        Layout layout = Layout::Keep;
        DuplicatePolicy duplicates = DuplicatePolicy::Keep;
        bool excludes = false;
        std::string patterns = "*.tmp\nThumbs.db";
        DeletionMode deletion = DeletionMode::Recycle;
        int inactiveMinutes = 0;
        SeedRule seedRule = SeedRule::Any;
        bool watches = false;
        std::string watchPath;
        bool watchesRecursively = false;
        std::string watchDestination;
        bool rechecksFinished = false;
        std::string language;
        std::string incompleteFolder;
        bool usesIncompleteFolder = false;
        bool appendsSuffix = true;
        bool confirmsExit = true;
        bool showsExternalIp = false;
        bool showsTitleSpeeds = false;
        bool showsFreeSpace = true;
        int refreshInterval = 1000;
        int recentInterval = 1;
        int historyInterval = 60;
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
        int activeTotal = 0;
        double capacityDownload = 0;
        double capacityUpload = 0;
        int torrentConnections = 0;
        bool ignoresSlow = true;
        int slowDownload = 2048;
        int slowUpload = 2048;
        int slowWait = 60;
        int activeChecking = 1;
        Transport transport = Transport::Both;
        IpFamily ipFamily = IpFamily::Both;
        int outgoingRate = 30;
        bool dht = true;
        bool pex = true;
        bool lsd = true;
        bool includesOverhead = true;
        bool limitsLan = true;
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
        bool notifiesBackground = true;
        bool preventsSleep = true;
        bool preventsSleepSeeding = false;
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
        std::string const& AdditionFolder() const;
        Limits Caps(LimitMode mode) const;
        // The mode's name in settings.json, the settings command and the snapshot.
        static char const* Name(LimitMode mode);
        static std::optional<LimitMode> Named(Json const& value);
    };

    struct PathLess
    {
        bool operator()(std::string const& left, std::string const& right) const;
    };

    // The saved document. A change edits a copy of the saved one and commits
    // it.
    struct Document
    {
        static constexpr int format = 1;

        Settings settings;
        std::map<std::string, Facts> torrents;
        std::vector<std::string> queueOrder;
        std::map<std::string, std::string, PathLess> watchedSources;

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
    // Workers may query libtorrent, so the session outlives their jobs.
    std::unique_ptr<lt::session> session;
    Store store;
    Store payload;
    // Reads preview sources, which can block on a slow share.
    Store sources;
    // Checks proxies, which can wait for one that does not answer.
    Store checks;
    // Serializes route cancellation with nonblocking proxy connect/send calls.
    std::mutex checkGate;
    // Ends proxy checks when their route changes or the engine closes.
    std::stop_source checkStop;
    struct ConnectionTest
    {
        std::string id;
        std::string connectionId;
        std::atomic<ConnectionPhase> phase{ConnectionPhase::Stopping};
        ConnectionPhase outcome = ConnectionPhase::Completed;
        std::stop_source stop;
        std::chrono::steady_clock::time_point deadline;
        std::optional<std::chrono::steady_clock::time_point> quietSince;
        std::int64_t sent = -1;
        std::int64_t received = -1;
        double download = 0;
        double upload = 0;
        std::string failure;
    };
    std::shared_ptr<ConnectionTest> connectionTest;
    Log log{store, directory};
    Changes changes{store, directory / L"settings.json", log};
    std::function<void()> wake;
    std::string sessionId = NewId();
    std::string externalIpv4;
    std::string externalIpv6;
    Settings settings = Defaults();
    bool launchPaused = false;
    std::map<std::string, std::string, PathLess> watchedSources;
    struct WatchedFile
    {
        std::string stamp;
        std::chrono::steady_clock::time_point readyAt;
        bool reported = false;
    };
    std::map<std::string, WatchedFile, PathLess> watchedFiles;
    std::chrono::steady_clock::time_point watchAt{};
    bool scanningWatch = false;
    bool addingWatch = false;
    bool watchFailed = false;
    std::vector<std::string> queueOrder;
    bool queuePending = false;
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
    bool statusPending = false;
    std::optional<ScheduleMode> scheduledMode;
    bool bypassesScheduledPause = false;
    std::optional<LimitMode> limitOverride;
    bool adapterMissing = false;
    std::string appliedListen;
    std::string appliedAdapter;
    std::optional<Settings::Proxy> appliedProxy;
    std::optional<Encryption> appliedEncryption;
    std::optional<Transport> appliedTransport;
    std::optional<IpFamily> appliedFamily;
    std::optional<bool> appliedLan;
    lt::peer_class_type_filter peerClasses;
    // The check of the proxy in use; nothing until it ends.
    std::optional<ProxyOutcome> proxyOutcome;
    // The check that check_proxy started last. The snapshot reports it,
    // because a check can take longer than the client waits for a reply.
    std::optional<RequestedCheck> requestedCheck;
    std::optional<bool> appliedPause;
    std::optional<LimitMode> appliedLimits;
    std::vector<std::string> limitingSeeds;
    std::vector<lt::torrent_handle> seedQueue;
    std::set<lt::torrent_handle> seedQueries;

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
        std::vector<std::filesystem::path> holds;
        std::vector<std::filesystem::path> moved;
        MovePhase phase = MovePhase::Staging;
    };
    struct Deletion
    {
        DeletionMode mode = DeletionMode::Permanent;
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
        std::size_t current = 0;
        std::filesystem::path source;
        std::filesystem::path target;
        RenamePhase phase = RenamePhase::Ready;
    };
    std::optional<Rename> rename;
    bool queryingFiles = false;
    // The detail of the torrent that torrent requests are reading. It is the
    // only detail the engine keeps, so nothing accumulates for other torrents.
    struct Reading
    {
        // A torrent request, with no view when it asks for the torrent itself.
        struct Read
        {
            std::string connectionId;
            std::string torrentId;
            std::optional<TorrentView> view;
            bool includeFiles = false;
            Reply reply;
            std::chrono::steady_clock::time_point started{};
            std::optional<std::int64_t> context;
        };
        // A read shows an answer at most this much older than itself, so a
        // section shown again after a while does not open on old peers and
        // speeds. Answers that arrive while it is held count until it is
        // answered, however long its other queries take.
        static constexpr auto answerAge = std::chrono::seconds(3);

        std::string torrentId;
        Detail detail;
        // Reads that wait for answers to the torrent's detail queries.
        std::vector<Read> held;
        // Reads of another torrent, which start once no read is held, because
        // replacing the detail would discard the answers the held reads need.
        std::vector<Read> waiting;
    };
    std::optional<Reading> reading;

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
    bool SuspendsForConnectionTest() const;
    Json ConnectionTestSnapshot() const;
    bool StartConnectionTest(std::string const& connectionId);
    void ReleaseConnectionTest(std::string const& connectionId, ConnectionPhase outcome = ConnectionPhase::Completed);
    void MaintainConnectionTest();
    void ObserveConnectionTest(lt::session_stats_alert const& alert);
    void MeasureConnection(std::shared_ptr<ConnectionTest> const& test);
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
    static std::optional<NetworkAdapter> FindAdapter(std::string const& name);
    void ApplyPolicy(lt::torrent_handle const& handle);
    ScheduleMode ScheduledMode() const;
    bool IsPaused() const;
    // Paused by the person or their schedule, which Resume all lifts; a missing
    // adapter pauses transfers too, but Resume all cannot lift that.
    bool IsPausedByChoice() const;
    LimitMode CurrentLimits() const;
    void LimitSeeds();
    void QuerySeeds();
    bool ReachedSeedLimit(Torrent const& torrent) const;
    void Configure(Json const& choices, std::function<void(Outcome)> completion);
    // Connects to the proxy and signs in, without changing the session.
    // `completion` receives nothing when the check cannot run.
    void CheckProxy(Settings::Proxy proxy, std::function<void(std::optional<ProxyCheck>)> completion);
    // The outcome's name in the snapshot.
    static std::string_view Name(ProxyOutcome outcome);
    static bool IsAbsolute(std::string const& path);
    void PauseSession(bool paused, std::function<void(Outcome)> completion);
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
    void AddSource(std::string source, std::function<void(Outcome, Added)> completion,
        std::string destination = {}, std::string watchStamp = {});
    void WatchFolder();
    void RecordWatch(std::string source, std::string stamp, std::function<void(Outcome)> completion);
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
    void Remove(std::vector<std::string> const& ids, Reply reply, bool deleteData = false,
        DeletionMode mode = DeletionMode::Permanent);
    std::list<Deletion>::iterator PrepareDeletion(std::vector<std::string> const& ids,
        DeletionMode mode = DeletionMode::Permanent);
    void RemoveHandles(std::vector<std::string> const& ids, std::list<Deletion>::iterator deletion);
    bool CanRemove(Torrent const& torrent) const;
    void RemoveDeferred();
    static std::filesystem::path FullPath(std::filesystem::path const& path);
    static bool PathBefore(std::filesystem::path const& left, std::filesystem::path const& right);
    static bool SamePath(std::filesystem::path const& left, std::filesystem::path const& right);
    static bool SameFolder(std::string const& left, std::string const& right);
    bool FilesBusy() const;
    static std::vector<std::filesystem::path> FilePaths(lt::torrent_info const& metadata,
        std::string const& folder, Layout layout = Layout::Keep);
    std::vector<std::filesystem::path> FilePaths(Torrent const& torrent,
        std::string const& destination = {}, bool logical = true) const;
    Scope FileScope(std::vector<std::string> const& ids) const;
    Json Describe(std::vector<std::string> const& ids, Scope const& scope) const;
    bool NamesReady();
    Outcome FilesReady(std::vector<std::string> const& ids);
    void StartMove(std::vector<std::string> const& ids, std::string const& destination,
        bool useExisting, std::function<void(Outcome)> accepted);
    void ContinueMove();
    void FinishMove(lt::torrent_handle const& handle, std::optional<Problem> problem);
    void RecoverMove();
    void ContinueDeletion();
    void Delete(std::list<Deletion>::iterator deletion);
    void On(lt::torrent_deleted_alert const& alert);
    void On(lt::torrent_delete_failed_alert const& alert);
    void RecoverFiles();
    void PrepareFiles(Torrent& torrent);
    void ReleaseFiles(std::vector<lt::torrent_handle> const& handles);
    static void PrepareNames(lt::add_torrent_params& params, bool appendsSuffix, Layout layout);
    // Returns whether any rename was requested.
    static bool ApplyNames(lt::torrent_handle const& handle, lt::add_torrent_params const& prepared,
        lt::renamed_files const& current, std::set<lt::file_index_t>& pending);
    void ContinueNames();
    void FinishNames(Torrent& torrent);
    void RecoverNames(Torrent& torrent);
    void QueryFiles();
    void CompleteFiles(Torrent& torrent, lt::span<std::int64_t const> progress);
    void FinishFiles(Torrent& torrent, std::optional<lt::file_index_t> after = {});
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
    void QueryPriorities(Torrent& torrent);

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
    void FinishCheckpoint(Torrent& torrent);
    void QueryCompletion(Torrent& torrent);
    void QueryCompletions();
    void ObserveCompletion(Torrent& torrent, lt::torrent_status latest);
    void FailCheckpoint(Torrent& torrent, Problem problem);
    void On(lt::save_resume_data_alert const& alert);
    void On(lt::save_resume_data_failed_alert const& alert);

    // Display reads answer immediately and receive their missing detail later;
    // action reads wait for the complete detail they need.
    void ReadDetail(Reading::Read read);
    void CollectDetail(Reading::Read read);
    // Asks libtorrent for that part of the torrent's detail, unless a query of
    // that kind is outstanding.
    void Query(Torrent& torrent, DetailKind kind);
    void ContinueReading();
    // Makes the torrent's detail queried until now obsolete, so the window,
    // which reads again after a command's reply, never sees the torrent as it
    // was before the change.
    void Invalidate(std::string const& torrentId);
    // Consumes the outstanding query and reports whether its answer is current.
    // Only the inspected torrent keeps a copy.
    bool Receive(lt::torrent_handle const& handle, DetailKind kind,
        std::function<void(Detail&)> const& store, std::function<void(Torrent&)> const& observe = {});
    // Answers each held read with the failure, then ends the reading.
    void FailReading(Json const& failure);
    // Drops the client's reads, and ends the reading once no read is held, so
    // the detail never outlasts the clients that read it.
    void ReleaseReading(std::string const& connectionId);
    // Forgets the detail, which holds no read, and starts the waiting reads.
    void EndReading();
    void RecoverDetail(lt::alerts_dropped_alert const& alert);
    void On(lt::tracker_list_alert const& alert);
    void On(lt::peer_info_alert const& alert);
    void On(lt::file_progress_alert const& alert);
    void On(lt::file_priorities_alert const& alert);
    void On(lt::piece_availability_alert const& alert);
    void On(lt::piece_info_alert const& alert);

    void Shutdown(std::function<void(std::optional<std::string> failure)> completion);
    void ContinueShutdown();
    void Finish();

    void Handle(lt::alert* alert);
    void On(lt::session_stats_alert const& alert);
    void On(lt::external_ip_alert const& alert);
    void On(lt::state_changed_alert const& alert);
    void On(lt::file_prio_alert const& alert);
    void On(lt::torrent_checked_alert const& alert);
    void On(lt::torrent_paused_alert const& alert);
    void On(lt::state_update_alert const& alert);
    void On(lt::cache_flushed_alert const& alert);
    void On(lt::torrent_conflict_alert const& alert);
    void On(lt::metadata_received_alert const& alert);
    void On(lt::torrent_error_alert const& alert);
    void On(lt::file_error_alert const& alert);
    void On(lt::alerts_dropped_alert const&);

private:
    void PrepareMove();
    void SaveMove();
    void FailMove(Problem problem);
    void EndMove(std::optional<Problem> problem);

    SpeedHistory history;

    void CommitEdit(std::string const& id,
        std::map<int, lt::download_priority_t> const& priorities,
        std::optional<std::vector<lt::announce_entry>> const& trackers, Reply reply);
};
}
