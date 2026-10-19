#pragma once

#include "Changes.h"
#include "Engine.h"
#include "Log.h"
#include "Payload.h"
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
struct ConnectionMeasurement
{
    std::chrono::seconds duration{5};
    std::atomic<std::chrono::steady_clock::time_point> started{};
    std::atomic<std::chrono::steady_clock::time_point> finished{};
    std::atomic<std::uint64_t> bytes{0};
    std::atomic<double> rate{0};
};

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
    // How long a failed save waits before it runs again.
    static constexpr auto checkpointRetry = std::chrono::seconds(30);

    struct Preview
    {
        std::string previewId;
        std::string connectionId;
        lt::add_torrent_params params;
        // The .torrent file the preview read; empty for a magnet.
        std::string torrentFile;
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
        // The watch folder's file that an addition reads, with the stamp the
        // engine records once it is added, so later scans skip it.
        struct Watch
        {
            std::string source;
            std::string stamp;
        };

        struct Choices
        {
            std::string destination;
            Intent intent = Intent::Resumed;
            std::vector<lt::download_priority_t> priorities;
            bool sequential = false;
            bool firstLast = false;
            bool queueTop = false;
            std::optional<Watch> watch;
        };

        std::string torrentId;
        lt::add_torrent_params params;
        Facts facts;
        bool queueTop = false;
        std::optional<Watch> watch;
        std::function<void(Outcome, Added)> completion;
        lt::torrent_handle handle;
        AdditionPhase phase = AdditionPhase::Adding;
        std::set<lt::file_index_t> renaming;
        // Whether the addition chose its file names. One that did not, such
        // as a magnet without metadata, leaves them to ContinueNames.
        bool named = false;
    };

    // The person's settings, saved in the document. A setting that the
    // document lacks or holds incorrectly keeps its default.
    struct Settings
    {
        struct Limits
        {
            int download = 0;
            int upload = 0;

            bool operator==(Limits const&) const = default;
        };

        struct Period
        {
            std::vector<int> days;
            int start = 0;
            int end = 0;
            ScheduleMode mode = ScheduleMode::Alternative;

            bool operator==(Period const&) const = default;
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
            // A proxy works only with its address, so a chosen type needs
            // its host and port.
            bool IsComplete() const { return type == ProxyType::None || (!host.empty() && port != 0); }
        };

        std::string destination;
        std::string lastFolder;
        bool startsDownload = true;
        bool queueTop = false;
        bool usesLastFolder = false;
        bool startsPaused = false;
        bool raisesAdd = true;
        Layout layout = Layout::Keep;
        DuplicatePolicy duplicates = DuplicatePolicy::Keep;
        bool excludes = false;
        std::string patterns = "*.tmp\nThumbs.db";
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
        bool showsMemory = false;
        int refreshInterval = 1000;
        int recentInterval = 1;
        int historyInterval = 60;
        int diskBufferMib = 100;
        int checkingMib = 4;
        int hashingThreads = 1;
        int fileLimit = 40;
        // The window, the splash and the popup follow the theme.
        Theme theme = Theme::System;
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
        // proxy.password as settings.json holds it, encrypted for the current
        // Windows user. Configure encrypts only a changed password, so a failed
        // encryption refuses that change and never another save.
        std::string encryptedPassword;
        double ratio = 0;
        int seedingMinutes = 0;
        bool checksUpdates = true;
        bool scheduleEnabled = false;
        std::vector<Period> schedule;
        bool showsTorrentDialog = true;
        bool showsMagnetDialog = true;
        bool confirmsDelete = true;
        // The Remove dialog's check boxes as the person last submitted them.
        bool deletesDownloaded = false;
        bool deletesTorrentFile = false;
        bool showsSplash = true;
        bool startsInTray = false;
        bool allPaused = false;
        Limits limits;
        Limits alternative;
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
        // The setting that holds each notification switch.
        static auto Switch(ToastSwitch toast) -> bool Settings::*;
    private:
        // Visits each setting that settings.json, the settings reply and the
        // settings command share, with its key and the values it accepts.
        template <typename Self, typename Visit>
        static void Fields(Self& settings, Visit&& visit);
    };

    struct PathLess
    {
        bool operator()(std::string const& left, std::string const& right) const;
    };

    struct TransferTotals
    {
        std::int64_t downloaded = 0;
        std::int64_t uploaded = 0;

        bool operator==(TransferTotals const&) const = default;
        Json ToJson() const;
    };

    // The saved document. A change edits a copy of the saved one and commits
    // it.
    struct Document
    {
        static constexpr int format = 1;

        Settings settings;
        TransferTotals transfers;
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

    // What the saved settings and the environment ask of the session, in the
    // parts that apply separately.
    struct Policy
    {
        // What the policy depends on besides the saved settings.
        struct Environment
        {
            // The chosen adapter, when one is chosen and Windows has it.
            std::optional<NetworkAdapter> adapter;
            // The session pauses for a reason other than a missing adapter,
            // which the policy finds itself.
            bool paused = false;
            LimitMode limits = LimitMode::None;
        };
        // How traffic leaves the computer. A change cancels the proxy checks
        // and reconnects.
        struct Route
        {
            Settings::Proxy proxy;
            // The adapter's name as libtorrent matches it; empty for any.
            std::string adapter;
            bool adapterMissing = false;

            bool operator==(Route const&) const = default;
        };
        // A change reconnects, because these apply only to new peers.
        struct Connection
        {
            std::string listen;
            std::string outgoing;
            Transport transport = Transport::Both;
            IpFamily family = IpFamily::Both;
            bool limitsLan = false;
            Encryption encryption = Encryption::Preferred;

            bool operator==(Connection const&) const = default;
        };
        struct Session
        {
            int activeDownloads = 0;
            int activeSeeds = 0;
            int activeTotal = 0;
            int activeChecking = 0;
            int diskBufferMib = 0;
            int checkingMib = 0;
            int hashingThreads = 0;
            int fileLimit = 0;
            bool ignoresSlow = false;
            int slowDownload = 0;
            int slowUpload = 0;
            int slowWait = 0;
            int outgoingRate = 0;
            int connections = 0;
            bool dht = false;
            bool lsd = false;
            bool includesOverhead = false;

            bool operator==(Session const&) const = default;
        };
        struct Torrents
        {
            int connections = 0;
            bool pex = false;

            bool operator==(Torrents const&) const = default;
        };
        // The sample intervals of the speed histories.
        struct Intervals
        {
            int recent = 0;
            int history = 0;

            bool operator==(Intervals const&) const = default;
        };

        Policy(Settings const& settings, Environment const& environment);
        bool operator==(Policy const&) const = default;

        Route route;
        Connection connection;
        // Port mapping, which applies without reconnecting.
        bool maps = false;
        Session session;
        Torrents torrents;
        Intervals intervals;
        Settings::Limits rates;
        bool paused = false;
    };

    // A command's work on a list of torrents that all exist.
    using Action = std::function<void(std::vector<std::string> const& ids, std::function<void(Outcome)> done)>;

    // Routine updates, seed limit queries and detail queries all answer in a
    // state_update_alert. Answered tells the answers apart by what only some
    // queries ask for: only a detail query asks for the save path, and only
    // seed and detail queries ask for the metadata.
    static constexpr lt::status_flags_t routineQuery = lt::torrent_handle::query_name;
    static constexpr lt::status_flags_t seedQuery = routineQuery | lt::torrent_handle::query_torrent_file;
    static constexpr lt::status_flags_t detailQuery = seedQuery | lt::torrent_handle::query_save_path |
        lt::torrent_handle::query_renamed_files | lt::torrent_handle::query_pieces;
    static StatusQuery Answered(lt::torrent_status const& status);

    // Declared before the members that they initialize, because IntelliSense
    // cannot find them otherwise.
    static std::string NewId();
    static Settings Defaults();

    std::filesystem::path directory;
    // Workers may query libtorrent, so the session outlives their jobs.
    std::unique_ptr<lt::session> session;
    Store store;
    Store payload;
    // Moves torrent files, which can take many minutes, so the payload
    // worker's checkpoints and renames do not wait for it.
    Store copies;
    // Reads preview sources, which can block on a slow share, and does the
    // disk work of previews and additions, which touches no other torrent's
    // files, so an addition never waits behind another torrent's file work.
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
        std::optional<TransferTotals> sampled;
        ConnectionMeasurement download;
        ConnectionMeasurement upload;
        bool complete = false;
        std::optional<ConnectionFailure> failure;
    };
    std::shared_ptr<ConnectionTest> connectionTest;
    Log log{store, directory};
    Changes changes{store, directory / L"settings.json", log};
    std::function<void()> wake;
    std::string sessionId = NewId();
    TransferTotals transferred;
    TransferTotals previousTransfers;
    TransferTotals savedTransfers;
    bool transfersPending = false;
    lt::time_point transfersRequested{};
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
    WatchPhase watchPhase = WatchPhase::Idle;
    bool watchFailed = false;
    bool watchSaveFailed = false;
    std::vector<std::string> queueOrder;
    bool queuePending = false;
    std::string language;
    // Listed torrents: they have rows, are saved, and accept commands.
    std::map<std::string, Torrent> torrents;
    // A torrent the person removed, out of the list and the saved document. It
    // stays until RemoveHandles gives its handle to libtorrent: in the
    // removal's own commit when nothing holds it, otherwise from
    // RemoveDeferred once CanRemove and, with a mode, NamesReady allow it.
    struct Removal
    {
        Torrent torrent;
        // Nothing when the person kept the files.
        std::optional<DeletionMode> mode;
    };
    std::map<std::string, Removal> removed;
    // The handles of listed torrents only.
    std::map<lt::torrent_handle, Torrent*> handles;
    std::vector<Toast> toasts;
    std::map<std::string, Preview> previews;
    std::map<std::string, Addition> additions;
    std::vector<std::shared_ptr<Preview>> parsing;
    Startup startup = Startup::Settings;
    ShutdownPhase shutdownPhase = ShutdownPhase::Idle;
    std::vector<lt::torrent_handle> pausing;
    // When the current shutdown phase started to wait.
    std::optional<std::chrono::steady_clock::time_point> phaseStarted;
    // Set during shutdown when the final save cannot complete: its cause, or
    // empty when the cause is unknown.
    std::optional<std::string> saveFailure;
    // Why startup failed, as the person sees it; it may be empty.
    std::string startupError;
    std::function<void(std::optional<std::string> failure)> shutdown;
    std::chrono::steady_clock::time_point checkpointAt = std::chrono::steady_clock::now();
    // The torrent CheckpointUnsaved chose last; the next choice starts after it.
    std::string checkpointCursor;
    std::chrono::steady_clock::time_point statusAt{};
    bool statusPending = false;
    bool statusOutstanding = false;
    bool snapshotPending = false;
    // Keep the cadence's phase when a collection tick arrives late.
    std::chrono::steady_clock::time_point snapshotAt = std::chrono::steady_clock::now();
    std::int64_t snapshotSequence = 0;
    std::map<std::string, Reply> snapshotSubscribers;
    std::optional<ScheduleMode> scheduledMode;
    bool bypassesScheduledPause = false;
    std::optional<LimitMode> limitOverride;
    // The policy the session last received; nothing before the first.
    std::optional<Policy> applied;
    lt::peer_class_type_filter peerClasses;
    // The check of the proxy in use; nothing until it ends.
    std::optional<ProxyOutcome> proxyOutcome;
    // The check that check_proxy started last. The snapshot reports it,
    // because a check can take longer than the client waits for a reply.
    std::optional<RequestedCheck> requestedCheck;
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
        // Members that had a marker before this move, so their files can
        // still be in two folders.
        std::vector<std::string> recovering;
        // Two members use one file, so a member's move can change the other's.
        bool shared = false;
        std::size_t current = 0;
        std::vector<std::filesystem::path> holds;
        std::vector<std::filesystem::path> moved;
        // The files in `moved` that no copy of a member's own files put
        // there, sorted.
        std::vector<std::filesystem::path> found;
        // Files that earlier members put at the destination, kept while
        // members share files. On one volume, a member that shares one no
        // longer has it in its old folder.
        std::vector<std::filesystem::path> placed;
        // The current member's bytes, which its worker writes while it copies.
        std::shared_ptr<Payload::Progress> progress;
        // Bytes per second copied between the last two status samples, and
        // the current member's copied bytes at the last one.
        double rate = 0;
        std::uint64_t sampled = 0;
        MovePhase phase = MovePhase::Staging;
    };
    struct Deletion
    {
        std::optional<DeletionMode> mode;
        std::vector<lt::torrent_handle> waiting;
        std::vector<std::filesystem::path> holds;
        std::vector<std::filesystem::path> files;
        std::vector<std::filesystem::path> roots;
        std::string names;
        DeletionPhase phase = DeletionPhase::Saving;
    };
    std::optional<Move> move;
    // Nothing while no torrent's files are being checked.
    std::optional<double> checkRate;
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
        // Cached readiness and action reads accept facts this much older than
        // the request. Display can use older facts while fresh queries finish.
        // Answers received during a read count until its other queries finish.
        static constexpr auto answerAge = std::chrono::seconds(3);

        std::string torrentId;
        Detail detail;
        bool changed = false;
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
    // Saves `edit` applied to the facts of `ids`, then applies it to the live
    // facts of each torrent whose saved facts changed and calls `done` with
    // those ids. With no change nothing is written and `done` runs at once.
    // `edit` runs twice, so it reads only the facts and what it captures.
    // Call once, inside a queued change.
    void CommitFacts(std::vector<std::string> const& ids,
        std::function<void(std::string const& id, Facts& facts)> edit,
        std::function<void(Outcome outcome, std::vector<std::string> const& changed)> done);
    TransferTotals TotalTransfers() const;
    void SaveTransfers();
    void Tick();
    void Maintain();
    bool SuspendsForConnectionTest() const;
    Json ConnectionTestSnapshot() const;
    bool StartConnectionTest(std::string const& connectionId, bool longer);
    void ReleaseConnectionTest(std::string const& connectionId, ConnectionPhase outcome = ConnectionPhase::Completed);
    void MaintainConnectionTest();
    void ObserveConnectionTest(lt::session_stats_alert const& alert);
    void MeasureConnection(std::shared_ptr<ConnectionTest> const& test);
    Torrent* Find(lt::torrent_handle const& handle);
    // A listed or removed torrent: the ones whose files the engine may still
    // change. Throws like map::at when neither holds the id.
    Torrent& Working(std::string const& id);
    Torrent const& Working(std::string const& id) const;
    Torrent* FindWorking(std::string const& id);
    Torrent const* FindWorking(std::string const& id) const;
    // For the alert handlers that write names, renames, marking and file
    // completion, which a removed torrent's release still depends on.
    Torrent* FindWorking(lt::torrent_handle const& handle);
    // Visits every listed torrent, then every removed one.
    template <class Visit> void EachWorking(Visit visit)
    {
        for (auto& [id, torrent] : torrents)
            visit(torrent);
        for (auto& [id, removal] : removed)
            visit(removal.torrent);
    }
    template <class Visit> void EachWorking(Visit visit) const
    {
        for (auto const& [id, torrent] : torrents)
            visit(torrent);
        for (auto const& [id, removal] : removed)
            visit(removal.torrent);
    }
    // A removal in `removed` that libtorrent still holds loads this content,
    // so a second copy is refused as torrent_removing.
    bool Removing(lt::info_hash_t const& hashes) const;
    Torrent& Install(std::string const& id, lt::torrent_handle handle, Facts facts,
        lt::add_torrent_params const& params);
    void Notify(ToastKind kind, Torrent const& torrent, std::string detail = {}, std::string code = {});
    void Notify(ToastKind kind, std::string name, std::string detail, std::string torrentId = {},
        std::string code = {}, std::string folder = {});
    tt::Activity Activity() const;
    Json Snapshot();
    void PublishSnapshot();
    void RequestStatus(bool immediate = true);
    std::optional<std::string> FindDuplicate(lt::info_hash_t const& hashes, std::string const& excluded = {}) const;
    static bool Overlaps(std::vector<std::string> const& hashes, std::vector<std::string> const& others);
    void RecordHashes(Torrent& torrent);
    void RecordHashes(Torrent& torrent, lt::info_hash_t const& hashes);

    // Applies the parts of the policy that changed since the last call.
    void RefreshPolicy();
    static std::optional<NetworkAdapter> FindAdapter(std::string const& name);
    void ApplyPolicy(lt::torrent_handle const& handle);
    ScheduleMode ScheduledMode() const;
    // Why the session is paused, or nothing when it is not. A pause that Resume
    // all lifts, the person's and then the schedule's, comes first; Resume all
    // cannot lift the other reasons.
    std::optional<PauseReason> Paused() const;
    // As Paused, with the adapter missing or not as given, for the policy that
    // finds that out itself.
    std::optional<PauseReason> Paused(bool adapterMissing) const;
    // The chosen adapter is missing, so transfers wait for it.
    bool IsAdapterMissing() const;
    LimitMode CurrentLimits() const;
    void LimitSeeds();
    void QuerySeeds();
    bool ReachedSeedLimit(Torrent const& torrent) const;
    void Configure(Json const& choices, std::function<void(Outcome)> completion);
    // Saves every setting at its default, as a new installation starts.
    void ResetSettings(std::function<void(Outcome)> completion);
    // Saves each switch in `toasts` as off, for the popup's and the window's
    // More menus alike.
    void TurnOff(std::vector<ToastSwitch> const& toasts, std::function<void(Outcome)> completion);
    // Saves `next` as the settings and applies it; `settled` runs once it
    // applies, before the snapshot is published.
    void SaveSettings(Settings next, std::function<void()> settled, std::function<void(Outcome)> completion);
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
    // A `watchStamp` marks a file from the watch folder, which saves to the
    // watch destination when one is set.
    void AddSource(std::string source, std::function<void(Outcome, Added)> completion,
        std::optional<std::string> watchStamp = std::nullopt);
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
    std::optional<std::string> MovingAddition(lt::torrent_handle const& handle) const;
    void RecoverAdditions();
    void On(lt::add_torrent_alert const& alert);
    void On(lt::storage_moved_alert const& alert);
    void On(lt::storage_moved_failed_alert const& alert);

    std::optional<ErrorCode> Refusal() const;
    void Execute(Json const& request, std::string const& connectionId, Reply reply);
    void MergeTrackers(Preview& preview, std::string const& torrentId, std::function<void(Outcome)> completion);
    void Act(std::vector<std::string> ids, std::function<void(Outcome)> completion, Action action);
    // Rechecks each torrent now, or once its files are no longer busy.
    void Verify(std::vector<std::string> const& ids, std::function<void(Outcome)> completion);
    void Recheck(Torrent& torrent);
    // Runs a torrent's verification and file choices that waited for its
    // files.
    void ApplyWaiting(Torrent& torrent);
    // `kept` counts the files a deletion keeps because other torrents use
    // them: 0 without a deletion, and nothing while names are not ready.
    void Remove(std::vector<std::string> const& ids,
        std::function<void(Outcome, std::optional<std::size_t> kept)> completion,
        std::optional<DeletionMode> mode, std::optional<DeletionMode> torrentFiles);
    std::list<Deletion>::iterator PrepareDeletion(std::vector<std::string> const& ids,
        std::optional<DeletionMode> mode, Scope scope);
    void RemoveHandles(std::vector<std::string> const& ids, std::list<Deletion>::iterator deletion);
    bool CanRemove(Torrent const& torrent, std::optional<DeletionMode> mode) const;
    // Takes a removed torrent out of the list into `removed`, where it stays
    // until RemoveHandles gives its handle to libtorrent.
    void Extract(std::string const& id, std::optional<DeletionMode> mode);
    void RemoveDeferred();
    static std::filesystem::path FullPath(std::filesystem::path const& path);
    static bool PathBefore(std::filesystem::path const& left, std::filesystem::path const& right);
    static bool SamePath(std::filesystem::path const& left, std::filesystem::path const& right);
    static bool SameFolder(std::string const& left, std::string const& right);
    bool FilesBusy() const;
    // A deletion or rename runs.
    bool DeletingOrRenaming() const;
    static std::vector<std::filesystem::path> FilePaths(lt::torrent_info const& metadata,
        std::string const& folder, Layout layout = Layout::Keep);
    // The torrent's files at `folder`, or without one at its save folder and
    // its recovery marker's destination.
    std::vector<std::filesystem::path> FilePaths(Torrent const& torrent,
        std::optional<std::string> const& folder = std::nullopt, bool logical = true) const;
    // Every path the torrent's files can have, logical and physical, at its
    // save folder and at its recovery marker's destination, sorted, each once.
    std::vector<std::filesystem::path> Footprint(Torrent const& torrent) const;
    Scope FileScope(std::vector<std::string> const& ids) const;
    Json Describe(std::vector<std::string> const& ids, Scope const& scope) const;
    bool NamesReady() const;
    Outcome FilesReady(std::vector<std::string> const& ids);
    void StartMove(std::vector<std::string> const& ids, std::string const& destination, MoveStart start,
        std::function<void(Outcome)> accepted);
    // Continues each interrupted move once, together with the torrents
    // whose interrupted move had the same destination.
    void ResumeMove();
    // Busy files and another torrent's missing metadata only delay a move,
    // because they clear by themselves; the second shows on the torrents.
    static bool DelaysMove(Outcome const& outcome);
    void MoveWaiting();
    void RefuseMove(Torrent& torrent, Outcome const& outcome);
    void ContinueMove();
    // `failure` is libtorrent's reason when it reports that the move failed.
    void FinishMove(lt::torrent_handle const& handle, std::string failure);
    void RecoverMove();
    // The share of the torrent's bytes that its move has placed: 0 while it
    // waits for its turn, and nothing when no move holds it.
    std::optional<double> MoveProgress(Torrent const& torrent) const;
    // Samples the move's copy rate over the time since the last sample.
    void SampleMove(std::chrono::steady_clock::duration elapsed);
    // Samples the bytes per second libtorrent checks across every torrent
    // whose files it checks, over the time since the last sample.
    void SampleChecks(std::chrono::steady_clock::duration elapsed);
    void ContinueDeletion();
    // Deletes the files in the mode, then the folders that leaves empty up to
    // the nearest of `roots`, even after a failure, and throws the first
    // failure. A file that is already gone counts as deleted.
    static void Erase(std::vector<std::filesystem::path> const& files, DeletionMode mode,
        std::vector<std::filesystem::path> const& roots);
    void Delete(std::list<Deletion>::iterator deletion);
    void DeleteTorrentFiles(std::vector<std::filesystem::path> files, DeletionMode mode, std::string names);
    void ReportDeletion(StorageOutcome const& outcome, std::string names, std::filesystem::path const& folder);
    void On(lt::torrent_deleted_alert const& alert);
    void On(lt::torrent_removed_alert const& alert);
    void On(lt::torrent_delete_failed_alert const& alert);
    void RecoverFiles();
    void RecoverDeletions();
    void ReleaseFiles(std::vector<lt::torrent_handle> const& handles);
    static void SyncCall(lt::session& session, std::function<void()> work);
    // The names a worker chose for a torrent's files in `params`, and the names
    // the files had when it chose them.
    struct NameChoice
    {
        lt::add_torrent_params params;
        lt::renamed_files current;
    };
    // Whether a file named `actual` still carries the suffix of an unfinished
    // download of the file `name`.
    static bool IsUnfinished(std::string const& actual, std::string const& name);
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
    void SetIntent(std::vector<std::string> const& ids, Intent intent, std::function<void(Outcome)> completion);
    // Saves `change` to each listed torrent's facts, then applies them.
    void ChangeFacts(std::vector<std::string> const& ids, std::function<void(Facts&)> const& change,
        std::function<void(Outcome)> completion);
    void Edit(std::string const& id, Json const& choices, std::function<void(Outcome)> completion);
    void QueryPriorities(Torrent& torrent);

    static bool IsQueued(lt::queue_position_t position);
    void AwaitQueueStatus(Torrent& torrent);
    std::vector<std::string> CurrentQueue() const;
    void ApplyQueue();
    void Queue(std::vector<std::string> const& ids, QueueMove move, std::string const& before,
        std::function<void(Outcome)> completion);
    static std::vector<std::string> Reorder(std::vector<std::string> order,
        std::vector<std::string> const& ids, QueueMove move, std::string const& before);

    void WriteResume(std::string const& id, lt::add_torrent_params params,
        std::function<void(StorageOutcome)> completion);
    void CheckpointUnsaved();
    void SaveCheckpoint(Torrent& torrent, lt::add_torrent_params params);
    void FinishCheckpoint(Torrent& torrent);
    void QueryCompletions();
    void ObserveCompletion(Torrent& torrent, lt::torrent_status latest);
    void FailCheckpoint(Torrent& torrent, Problem problem);
    void On(lt::save_resume_data_alert& alert);
    void On(lt::save_resume_data_failed_alert const& alert);

    // Display reads answer immediately and receive their missing detail later;
    // action reads wait for the complete detail they need.
    void ReadDetail(Reading::Read read);
    void CollectDetail(Reading::Read read);
    // Asks libtorrent for that part of the torrent's detail, unless a query of
    // that kind is outstanding.
    void Query(Torrent& torrent, DetailKind kind);
    void ContinueReading(bool changed = false);
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
    // Display release preserves actions awaiting complete detail; disconnect
    // drops both because their replies can no longer reach the client.
    void ReleaseReading(std::string const& connectionId, bool disconnected = false);
    // Forgets the detail, which holds no read, and starts the waiting reads.
    void EndReading();
    void RecoverDetail(lt::alerts_dropped_alert const& alert);
    void On(lt::tracker_list_alert const& alert);
    void On(lt::peer_info_alert& alert);
    void On(lt::file_progress_alert const& alert);
    void On(lt::file_priorities_alert const& alert);
    void On(lt::piece_availability_alert const& alert);
    void On(lt::piece_info_alert const& alert);

    bool IsShuttingDown() const { return shutdownPhase != ShutdownPhase::Idle; }
    bool IsLoading() const { return startup == Startup::Settings || startup == Startup::Transfers; }
    void Shutdown(std::function<void(std::optional<std::string> failure)> report);
    void StartFinalSave();
    void FailFinalSave(std::optional<std::string> cause = std::nullopt);
    void ContinueShutdown();
    void RetryFinalSave();
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
    // The torrent's physical files whose path changes at the move's destination.
    std::vector<Payload::File> Relocated(Torrent const& torrent) const;
    void SaveMove();
    void CleanMove(std::vector<std::filesystem::path> sources, std::filesystem::path origin);
    void FailMove(Problem problem);
    void EndMove(Torrent& torrent, std::optional<Problem> problem);

    SpeedHistory history;

    void CommitEdit(std::string const& id,
        std::map<int, lt::download_priority_t> const& priorities,
        std::optional<std::vector<lt::announce_entry>> const& trackers, std::function<void(Outcome)> completion);
};
}
