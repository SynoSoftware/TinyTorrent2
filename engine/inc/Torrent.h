#pragma once

#include "Engine.h"
#include "Enums.h"
#include "SpeedHistory.h"
#include <libtorrent/add_torrent_params.hpp>
#include <libtorrent/announce_entry.hpp>
#include <libtorrent/peer_info.hpp>
#include <libtorrent/torrent_handle.hpp>
#include <libtorrent/torrent_status.hpp>
#include <chrono>
#include <cstdint>
#include <filesystem>
#include <functional>
#include <future>
#include <map>
#include <memory>
#include <optional>
#include <set>
#include <string>
#include <variant>
#include <vector>

namespace tt
{
struct StorageOutcome;

struct Problem
{
    ProblemKind kind;
    std::string detail;
    // Why a move did not start. The row and the toast report it instead of
    // the kind, so the window can say why.
    std::optional<ErrorCode> refusal;

    // A failed storage job's problem: `kind` with the failure's detail, or
    // StorageOverloaded when the busy store refused the job, which the
    // failure reports as Overloaded.
    static Problem From(Outcome const& failure, ProblemKind kind);
    static Problem From(StorageOutcome const& failure, ProblemKind kind);
    static Problem From(lt::error_code const& error);
    char const* Code() const;
    bool IsStorage() const;
    bool operator==(Problem const&) const = default;
};

char const* ToString(ProblemKind kind);

// The longest list of skip patterns, in characters, that the settings and a
// torrent's facts keep.
inline constexpr std::size_t patternLimit = 4096;

// What the document saves about a torrent, apart from its identity.
struct Facts
{
    // The recovery marker of a move.
    struct Marker
    {
        // The bytes at the start of an unfinished copy that a flush proved on
        // disk, which a later move continues from; see `Payload::Move`.
        struct Copy
        {
            // The copy's final path.
            std::string path;
            std::uint64_t bytes = 0;
        };

        std::string destination;
        // How far the move got.
        MoveStage stage = MoveStage::Staged;
        std::optional<Copy> copy;
        // The files that the preflight found at the destination before the
        // move changed any file there, which are the person's own.
        std::vector<std::string> found;
        // The move failed by itself and left files in both folders, so it
        // waits for the person, not for the next start.
        bool failed = false;

        // The move or its give-back failed. A failure can delete the
        // unfinished copies, so their proof goes too.
        void Fail()
        {
            failed = true;
            copy.reset();
        }
    };

    std::string savePath;
    std::string finalFolder;
    bool appendsSuffix = true;
    Layout layout = Layout::Keep;
    std::string skipPatterns;
    std::optional<Marker> marker;
    // The .torrent file it was added from, which Removal can delete; empty
    // for a magnet. The file may have moved or gone since.
    std::string torrentFile;
    bool verifyFiles = false;
    Intent intent = Intent::Resumed;
    bool ignoresSeedLimits = false;
    // When it was added, in seconds since 1970.
    std::int64_t added = 0;
    // Empty means the default priorities, once the metadata is known.
    std::vector<lt::download_priority_t> priorities;
    std::vector<std::string> hashes;
    // No choice retains the resume trackers; an empty choice removes them.
    std::optional<std::vector<lt::announce_entry>> trackers;
    bool sequential = false;
    bool firstLast = false;
    // In bytes a second; 0 means no limit.
    int downloadLimit = 0;
    int uploadLimit = 0;

    Json ToJson() const;
    static Facts Read(Json const& saved);
};

// libtorrent's latest answers to the detail queries for one torrent.
struct Detail
{
    bool Has(DetailKind kind, std::chrono::steady_clock::time_point since = {}) const
    {
        auto found = received.find(kind);
        return found != received.end() && found->second >= since;
    }
    // When each kind's answer arrived; a kind without one has no answer.
    std::map<DetailKind, std::chrono::steady_clock::time_point> received;
    std::vector<lt::announce_entry> trackers;
    std::vector<lt::peer_info> peers;
    std::vector<lt::known_peer> knownPeers;
    std::vector<std::int64_t> progress;
    std::vector<lt::download_priority_t> priorities;
    lt::torrent_status status;
    std::vector<int> availability;
    // The bytes received of each piece being downloaded.
    std::map<lt::piece_index_t, std::int64_t> downloading;
};

struct FileNames
{
    std::shared_ptr<lt::torrent_info const> metadata;
    lt::renamed_files mappings;
};

// A move that waits for busy files or its drives: one the person asked for,
// or the move to the final folder. The torrents of one request share its turn,
// their place among the jobs that wait for drives.
struct WaitingMove
{
    std::string destination;
    std::uint64_t turn = 0;
    // Why the move waits, which the row and a toast name: another torrent
    // whose folder overlaps the destination still acquires its metadata, or
    // the give-back of the torrent's cut move, which the move waits for,
    // failed.
    std::optional<Problem> refusal;
    // The engine's own move of a finished torrent to its final folder, whose
    // failure waits for the minute check instead of the person.
    bool final = false;
};

// What a waiting check or move waits for: a drive, named by the letter or
// server of the job's own folder, and the torrent whose running job uses it;
// or, for a move whose drives are free, the name of the torrent whose file
// work it waits for.
struct DriveWait
{
    std::string drive;
    std::string holderId;
    std::string files;
};

// A running job's progress at the last drive pass, and when the pass saw it
// change, so a job that stops making progress gives up.
struct Pulse
{
    std::uint64_t done = 0;
    std::chrono::steady_clock::time_point changed;
};

// How far a torrent has got with its files: the data it has, the data a
// finished torrent uploaded, and how far a check read. A download's upload
// does not count, because a download whose drive is full keeps uploading.
struct FileProgress
{
    std::int64_t done = 0;
    std::int64_t uploaded = 0;
    int checked = 0;

    static FileProgress Of(lt::torrent_status const& status);
    bool Beyond(FileProgress const& earlier) const;
    bool operator==(FileProgress const&) const = default;
};

// A check's place among the jobs that use drives, and its progress while the
// drive pass lets it run. A check that does not run waits for its drive.
struct CheckTurn
{
    std::uint64_t turn = 0;
    std::optional<Pulse> running;
};

// Whether a torrent's download has finished and reached the disk, from
// libtorrent's state changes, file completions and status samples. Each
// observation returns the one step the engine must take next.
class Completion
{
public:
    using State = lt::torrent_status::state_t;

    // Returns true when it needs a fresh status sample.
    bool Changed(State state, State previous);
    // A routine status update: payload growth while downloading counts as
    // receipt.
    void Sampled(lt::torrent_status const& previous, lt::torrent_status const& latest);
    // The fresh sample it asked for. Returns true while the download waits
    // for its write to disk, so the engine asks libtorrent to flush it.
    bool Observed(lt::torrent_status const& previous, lt::torrent_status const& latest);
    // Returns true when the file finished by downloading. Checking also
    // finishes files, and those count as nothing received.
    bool FileFinished();
    void Checked();
    // Returns true when the written download now settles its files.
    bool Flushed();
    void Rechecked();
    void Replaced();
    // Alerts were dropped: it needs a fresh sample, and a lost state change
    // leaves later file completions without a known cause.
    void Lost(bool stateChanges);
    // Returns true when a settling download starts its recheck.
    bool BeginCheck();
    void FailCheck();
    // Returns true when the download is complete, so the Completed toast is
    // due.
    bool Finish();

    bool NeedsSample() const { return requested_; }
    // The torrent is complete and no download, write or recheck is still
    // working on it.
    bool IsReached(lt::torrent_status const& status) const;
    bool IsWorking() const;
    // Downloading, or writing what it downloaded.
    bool IsDownloading() const;
    bool IsChecking() const { return phase_ == CompletionPhase::Checking; }
    bool IsIdle() const { return phase_ == CompletionPhase::Idle; }
    bool IsSettling() const { return phase_ == CompletionPhase::Settling; }

private:
    CompletionPhase phase_ = CompletionPhase::Idle;
    bool requested_ = false;
    // Payload arrived since the download began.
    bool received_ = false;
    // A replacement's session byte counter distinguishes new payload from a recheck.
    bool replaced_ = false;
    // The latest state change, the cause of the file completions after it.
    std::optional<State> origin_;
};

// A torrent's storage control: its storage failure, the failure of its move
// to the final folder, and its storage job. Only Decide changes them, one
// event at a time, and it returns what the engine must do. It reads no clock,
// handle or disk, and it never resumes a torrent: the engine applies the
// person's intent with the holds after each change.
class Control
{
public:
    // Where a failure stands. Both failures climb one ladder: one automatic
    // continue at once, then a stop that the minute check continues once,
    // then the person. A failed final-folder move enters at the stop.
    struct Ladder
    {
        FailureStage stage = FailureStage::Recovering;
        StorageRetry level = StorageRetry::Automatic;

        // The engine continues the torrent: at once from Recovering, and
        // after the minute check from Stopped.
        void Continue();
        // The failure returned after a continue, or waiting cannot fix it.
        void Stop(bool retryable);
        // The failure returned before the torrent got further, so it recovers
        // again at the same level.
        void Return();
        // The location stopped answering. Waiting fixes that, so the next
        // continue is automatic again.
        void LoseLocation();
        // The location answers again.
        void FindLocation();
        // The failure waits at Stopped or Person, so it shows the Retry
        // button.
        bool Waits() const;
        bool operator==(Ladder const&) const = default;
    };

    // A storage failure, from its first error until the torrent gets
    // further, other work removes its cause, or the person answers.
    struct Failure
    {
        Ladder ladder;
        // What failed last. A Continued failure hides it; it shows again if
        // the failure returns.
        Problem cause;
        bool retryable = true;
        // Where the torrent was when the engine continued it. Empty until
        // the first progress after a reload is known. Progress, not time,
        // tells a returning failure from a new one, so an error that returns
        // at once and one that Windows takes minutes to report are judged
        // alike.
        std::optional<FileProgress> progress;

        bool operator==(Failure const&) const = default;
    };

    // A failure's job in flight: the storage failure's release, probe,
    // minute check or reload, or the failed move's final-folder check. Each
    // result carries the record of its job, and a result whose record is no
    // longer one of the torrent's jobs changes nothing.
    struct Job
    {
        JobKind kind = JobKind::Release;
    };

    // What the engine knows of the torrent now.
    struct Reality
    {
        // Nothing while libtorrent checks the resume data.
        std::optional<FileProgress> progress;
        // libtorrent checks the files, so a continue reloads the torrent from
        // its resume data, which continues the check after its pieces.
        bool checking = false;
        // A move, a rename, name preparation or a mark uses the files, so no
        // probe, check or continue starts.
        bool fileWork = false;
    };

    // The folder does not answer.
    struct Absent
    {
    };
    // The folder answers, but the cause remains: the failed file does not
    // open with the access the torrent needs, the folder takes no new file,
    // or the drive lacks room.
    struct Unusable
    {
    };
    // The folder answers and, for the minute check, takes the torrent's files
    // again.
    struct Usable
    {
        // The files whose data is gone; none for the final folder. A result
        // of the torrent's own job records them, also after its failure
        // ended.
        std::vector<lt::file_index_t> lost;
    };
    // What a minute check found at a folder. A probe finds only whether the
    // folder answers.
    using Finding = std::variant<Absent, Unusable, Usable>;

    // A disk error, a status error, a stalled check, failed name
    // preparation, or a rename whose folder did not answer or whose error no
    // wait fixes. Once one error of a failure is one that no wait fixes, the
    // failure stays so.
    struct Failed
    {
        Problem cause;
        bool retryable = true;
    };
    struct Released
    {
        std::shared_ptr<Job const> job;
    };
    // Nothing when the probe's worker failed.
    struct Probed
    {
        std::shared_ptr<Job const> job;
        std::optional<std::variant<Absent, Usable>> finding;
    };
    // The torrent's own folder or the final folder, by the job's kind;
    // nothing when the check's worker failed.
    struct Checked
    {
        std::shared_ptr<Job const> job;
        std::optional<Finding> finding;
    };
    // Nothing when the torrent got its new handle.
    struct Reloaded
    {
        std::shared_ptr<Job const> job;
        std::optional<Problem> failure;
    };
    // The person's answer, or the torrent's removal.
    struct Answered
    {
        FailureAnswer answer = FailureAnswer::Retry;
    };
    // Other work removed this cause, such as names chosen at last.
    struct Cleared
    {
        Problem cause;
    };
    // A move gave the torrent its new folder, so its failure ends and the
    // result of a job on the old folder changes nothing.
    struct Replaced
    {
    };
    // libtorrent's handle of the torrent was replaced, by a new handle or by
    // the parameters of an add that failed, so a release, probe or minute
    // check on the old one ends. The reload that replaced it ends with its
    // own result.
    struct Dropped
    {
    };
    // Once a second for a torrent with a failure; `minute` once a minute.
    struct Due
    {
        bool minute = false;
    };
    // `final`: the move is the engine's own move to the final folder. Any
    // other move ends the failed move to the final folder.
    struct MoveStarted
    {
        bool final = false;
    };
    struct MoveEnded
    {
        std::optional<Problem> failure;
        // The engine's own move to the final folder failed and left the
        // files complete where they were, so it can run again by itself.
        bool final = false;
    };
    using Event = std::variant<Failed, Released, Probed, Checked, Reloaded, Answered, Cleared, Replaced, Dropped,
        Due, MoveStarted, MoveEnded>;

    struct Effects
    {
        // The jobs to start, each now its failure's job: at most one for each
        // failure.
        std::vector<std::shared_ptr<Job const>> jobs;
        // libtorrent's error and upload mode clear, so the torrent can run.
        bool clear = false;
        // The failed move to the final folder may run again.
        bool retryMove = false;
        // The control state changed, so the engine applies the torrent's
        // running state and shows the change.
        bool changed = false;
    };

    // Changes the control state for one event and returns what the engine
    // must do.
    Effects Decide(Event const& event, Reality const& reality);

    // The failures, for the engine's log and the transition check. The
    // failed move to the final folder has its cause in the torrent's
    // `moveError`.
    std::optional<Failure> const& Storage() const { return storage_; }
    std::optional<Ladder> const& FinalMove() const { return finalMove_; }
    // The storage failure's job.
    std::shared_ptr<Job const> Running() const { return job_; }
    // The failed move's final-folder check.
    std::shared_ptr<Job const> Checking() const { return checkJob_; }
    // The job is one of the torrent's current jobs, so its result counts.
    bool Owns(std::shared_ptr<Job const> const& job) const;
    // The torrent stays paused: a storage failure that is not Continued, a
    // release or reload that uses its files, or lost files that wait for
    // their reload. A held torrent reports no new status error, so a reload
    // that fails while no failure holds the torrent starts one.
    bool Holds() const;
    // Files that lost their data wait for their reload, which waits for the
    // torrent's file work to end. Until then the torrent stays paused and
    // does not finish; other file work may run.
    bool AwaitsReload() const;
    // A release or reload uses the torrent's files.
    bool Busy() const;
    // Storage recovery owns the torrent's files: a failure that recovers, or
    // a release or reload.
    bool Recovers() const;
    // The storage failure's cause, while the failure holds the torrent.
    std::optional<Problem> Cause() const;
    // A failure waits at Stopped or Person, so it shows the Retry button.
    bool NeedsPerson() const;
    // The storage failure waits at Stopped or Person, so the torrent does
    // not use its folder until the minute check or the person continues it.
    bool StorageWaits() const;
    // A recovering failure waits for a location that does not answer.
    bool AwaitsLocation() const;
    // A failure needs the engine's Due.
    bool Failing() const;
    // Due starts a probe once the torrent's files are free.
    bool WantsProbe() const;
    // The files whose data is gone. Each checkpoint leaves out their pieces
    // until a reload drops them, so a start after a cut downloads only those
    // files again instead of checking every one.
    std::vector<lt::file_index_t> const& Lost() const { return lost_; }

    bool operator==(Control const&) const = default;

private:
    void On(Failed const& failed, Reality const& reality, Effects& effects);
    void On(Released const& released, Reality const& reality, Effects& effects);
    void On(Probed const& probed, Reality const& reality, Effects& effects);
    void On(Checked const& checked, Reality const& reality, Effects& effects);
    void On(Reloaded const& reloaded, Reality const& reality, Effects& effects);
    void On(Answered const& answered, Reality const& reality, Effects& effects);
    void On(Cleared const& cleared, Reality const& reality, Effects& effects);
    void On(Replaced const& replaced, Reality const& reality, Effects& effects);
    void On(Dropped const& dropped, Reality const& reality, Effects& effects);
    void On(Due const& due, Reality const& reality, Effects& effects);
    void On(MoveStarted const& started, Reality const& reality, Effects& effects);
    void On(MoveEnded const& ended, Reality const& reality, Effects& effects);
    // Ends the job in `slot` when `job` is its record of `kind`; false for a
    // late result.
    static bool End(std::shared_ptr<Job>& slot, std::shared_ptr<Job const> const& job, JobKind kind);
    static void Start(std::shared_ptr<Job>& slot, Job job, Effects& effects);
    // Adds the files a probe or minute check found without their data.
    void Lose(std::vector<lt::file_index_t> lost);
    // The ladder, once the location answers a recovering failure.
    void Judge(Reality const& reality, Effects& effects);
    // Continues the torrent at the next level, through a reload when the
    // files lost data or libtorrent checks them.
    void Continue(Reality const& reality, Effects& effects);

    std::optional<Failure> storage_;
    std::optional<Ladder> finalMove_;
    std::shared_ptr<Job> job_;
    std::shared_ptr<Job> checkJob_;
    std::vector<lt::file_index_t> lost_;
};

// A torrent in the list: its libtorrent handle, the facts the document saves
// about it, and what the engine knows about its errors and checkpoints.
struct Torrent
{
    using WorkQuery = std::function<std::optional<tt::PathWork>(std::string const&)>;

    Torrent(std::string torrentId, WorkQuery work);

    // A torrent that libtorrent does not hold yet: the parameters to add it
    // from, and why libtorrent refused the last add of them. RestorePending
    // adds it once its folder's lookup answers and no other torrent holds
    // its identity; after a refusal, only once the person's Retry, Start or
    // Force clears it.
    struct Waiting
    {
        lt::add_torrent_params params;
        std::optional<Problem> refusal;
    };

    std::string torrentId;
    // Valid exactly while `waiting` is empty; only Attach and WaitForAdd
    // change either.
    lt::torrent_handle handle;
    // The root whose disk threads serve the handle's storage, as the engine
    // last gave it to libtorrent at the add, a move or Rekey. Work is keyed
    // by the root of whoever does its I/O: work on libtorrent's threads,
    // such as a check, by this key, and the engine's own calls on real paths
    // by the RootOf key of their folder.
    struct StorageRoot
    {
        // The RootOf key that libtorrent got.
        std::string key;
        // The NoticeRoot of the folder then. The torrent's location notice
        // and root probe name it.
        std::string notice;
        // When Rekey found the folder on another root. libtorrent moves the
        // storage only once its jobs on this root have returned, so Rekey
        // moves it once this root answers a probe that started after then.
        std::optional<std::chrono::steady_clock::time_point> left;
    };
    StorageRoot storageRoot;
    Facts facts;
    std::optional<Waiting> waiting;
    std::optional<FileNames> names;
    std::string comment;
    std::string creator;
    std::int64_t created = 0;
    // The add/resume list applies until the person chooses a replacement.
    std::vector<lt::announce_entry> sourceTrackers;
    lt::torrent_status status;
    lt::time_point statusAt{};
    QueueReport queueReport = QueueReport::Reported;
    SpeedHistory history;
    CheckpointPhase checkpointPhase = CheckpointPhase::Idle;
    std::optional<lt::add_torrent_params> pendingCheckpoint;
    bool unsaved = true;
    std::chrono::steady_clock::time_point retryAt{};
    std::int64_t savedUploaded = 0;
    // The resume file's share of pieces, derived for the row: libtorrent
    // reports no progress until it has checked that file, which waits while
    // the drive or share does not answer.
    std::optional<float> savedProgress;
    std::optional<Problem> checkpointError;
    std::optional<Problem> hashError;
    std::string conflict;
    lt::error_code diskError;
    lt::time_point diskClearedAt{};
    Control control;
    // libtorrent's fence after the last release of the files that the engine
    // asked for. The engine thread polls it, so no worker waits on it.
    std::shared_future<void> released;
    std::optional<Problem> notifiedError;
    NamePhase namePhase = NamePhase::Pending;
    std::optional<Problem> nameError;
    // Verification and file choices that wait until the files are not busy.
    bool needsRecheck = false;
    bool prioritiesPending = false;
    std::optional<WaitingMove> waitingMove;
    // The kind of its work that the last end left unfinished, while that work
    // waits for the person to continue or abandon it: a move, which leaves
    // the torrent where it is, or a check, which the engine holds.
    std::optional<TaskKind> unfinished;
    // The check that libtorrent started keeps its turn, also while Pause all
    // lasts, until it ends or the person stops it.
    std::optional<CheckTurn> checkTurn;
    // Whether libtorrent may still read the files for this torrent's check:
    // from a status that shows the check running until libtorrent reports
    // that its paused check has no read left, or until the check ends.
    CheckReads checkReads = CheckReads::None;
    // Since when its check's reads remain while the check does not run, as
    // after a stop or a removal; nothing otherwise.
    std::optional<std::chrono::steady_clock::time_point> readsSince;
    std::set<lt::file_index_t> completedFiles;
    bool filesPending = false;
    // The files whose rename_file request waits for its alert.
    std::set<lt::file_index_t> renaming;
    // The finished files whose Zone.Identifier stream is being written.
    std::set<lt::file_index_t> marking;
    std::chrono::steady_clock::time_point renameAt{};
    // The torrent stays paused while Flushing.
    CacheWrite cacheWrite = CacheWrite::Pending;
    Completion completion;
    // The bytes libtorrent had checked at the last status sample, and
    // nothing while it does not check the files.
    std::optional<std::uint64_t> checked;
    std::function<void(Outcome)> priorityCompletion;
    bool piecesPending = false;
    // Reads the operation owner; its lifetime, rather than a second flag,
    // determines when the torrent's paths are busy.
    std::optional<tt::PathWork> PathWork() const;
    // The last move's failure. While the engine's own move to the final
    // folder waits for the minute check, it is that failure's cause.
    std::optional<Problem> moveError;
    // Rises with each reply to a command that changes the torrent, and when
    // its metadata arrives, so no detail queried before then is shown.
    std::uint64_t generation = 0;
    // The generation at which each outstanding detail query was sent. An
    // answer carries only its torrent, so one query of each kind is
    // outstanding at a time and the record stays until its answer arrives.
    std::map<DetailKind, std::uint64_t> queries;

    std::string Name() const;
    // Every hash the torrent is known by: its own and those the document saved.
    std::vector<std::string> Hashes() const;
    std::string Folder() const;
    // Calls `visit` with the path of each file that is not padding: where
    // libtorrent stores it, or with `logical` where the layout puts it.
    // Nothing until its metadata, and with it its stored names, is known.
    void VisitPaths(bool logical, std::function<void(std::string)> const& visit) const;
    std::vector<std::filesystem::path> Paths(bool logical = false) const;
    // The folders its files can lie in: its save folder, and the destination
    // of the move its marker records.
    std::vector<std::string> Folders() const;
    // The first part of each of Paths(logical), as Tops gives them.
    std::vector<std::string> Tops(bool logical = false) const;
    std::optional<Problem> Error() const;
    std::optional<Problem> StorageProblem() const;
    // A storage failure, a failed move to the final folder or an alias
    // conflict stopped the torrent, or libtorrent did not add it.
    bool NeedsPerson() const;
    std::optional<Problem> Diagnose() const;
    // A move waits for its turn, or for the person's answer about the work
    // that the last end left unfinished, with no problem that delays it.
    bool MoveWaits() const;
    // Its row shows its path work as Moving. A give-back that waits for its
    // root or for the person keeps the files, but the person's waiting Move
    // shows why it does not start.
    bool ShowsPathWork() const;
    // The torrent's own files are being prepared, renamed, moved or marked.
    bool FileWork() const;
    // The torrent's file names are being prepared, renamed or marked, apart
    // from its path work.
    bool NameWork() const;
    // File work, or a storage release or reload, uses the torrent's files.
    bool FilesBusy() const;
    // New work on the torrent's files waits: the torrent waits for its add,
    // storage recovery owns its files, or other work uses them. Renames
    // also wait while a stop holds the torrent, because a stop keeps the
    // torrent off its folder; name preparation does not, because names
    // chosen at last end a stop they caused.
    bool WorkWaits() const;
    bool MoveBlocked() const;
    // The person's Start, Stop, Verify or Retry, or the removal, answers a
    // move problem. One that left no interrupted move clears, because its
    // files are where they were.
    void AnswerMoveProblem();
    // The person's Start, Force or Retry clears the conflict and the failed
    // add, as part of the engine's Answer.
    void Retry();
    // libtorrent holds the torrent now, through `added` on `root`.
    void Attach(lt::torrent_handle added, StorageRoot root);
    // libtorrent no longer holds the torrent, which waits to be added from
    // `params`.
    void WaitForAdd(lt::add_torrent_params params);
    // The engine keeps it paused for its own work, whatever the person chose:
    // a conflict, a move, the write before a rename, a check that waits for
    // its drive or for the person's answer, its storage control, or names
    // that wait for preparation once it has `hasMetadata`.
    bool IsHeld(bool hasMetadata) const;
    // The engine holds its check, because another job uses its drive.
    bool CheckWaits() const;
    // Its current file names are read, or it has no metadata to name files.
    bool NamesKnown() const;
    // It is not in libtorrent yet and RestorePending adds it by itself, as at
    // the start until its folder's lookup answers: libtorrent did not refuse
    // it, and no alias identity conflicts with it. Its commands wait for the
    // add, or reply `starting` where only libtorrent holds the answer.
    bool AwaitsAdd() const;
    // It waits to be added while another torrent holds its identity, so its
    // commands reply `alias_conflict`. One that libtorrent refused waits for
    // the person's Retry instead, and its commands act on its saved
    // parameters as for one that waits for its add.
    bool HeldByAlias() const;
    // Its metadata: the saved one while it is not in libtorrent, otherwise
    // libtorrent's, through a call to libtorrent's thread. Nothing for a
    // magnet that has none yet.
    std::shared_ptr<lt::torrent_info const> Metadata() const;
    // libtorrent checks its files now, as the last status reports.
    bool ChecksFiles(bool sessionPaused) const;
    Status Classify(bool sessionPaused) const;
    // The status without a move that runs or waits.
    Status ClassifyTransfer(bool sessionPaused) const;
    bool IsChanged() const;
    // Takes the newest status, and remembers when it shows new payload.
    void Update(lt::torrent_status latest, lt::time_point at);
    // The torrent's facts with its General detail.
    Json Describe(Detail const& detail, std::shared_ptr<lt::torrent_info const> const& metadata) const;
    Json Describe(TorrentView view, Detail const& detail,
        std::shared_ptr<lt::torrent_info const> const& metadata,
        std::chrono::steady_clock::time_point since = {}) const;
    // One `files` reply: the page of files that starts at `offset`, each with
    // its index, size, padding, pieces and chosen priority and, with `paths`,
    // its path and its current name on disk once its names are read.
    Json DescribeFilePage(std::shared_ptr<lt::torrent_info const> const& metadata, int offset, bool paths) const;
    // The file's current name on disk, or null until its names are read.
    Json DiskPath(lt::file_index_t index) const;
    // `queue` is the torrent's place in the download queue, or -1 outside it.
    // `moveProgress` replaces the progress while the torrent's files move.
    // `retry` offers the person's Retry.
    Json Row(bool sessionPaused, int queue, std::optional<double> moveProgress, DriveWait const& wait,
        bool retry) const;
    // The priority each file downloads with: the saved choice, or the
    // default with the skip patterns applied while none is saved. Every file
    // is skipped without metadata.
    std::vector<lt::download_priority_t> ChosenPriorities(
        std::shared_ptr<lt::torrent_info const> const& metadata) const;
    // The masks that ChosenPriority applies: those of the skip patterns while
    // no choice is saved, and otherwise none.
    std::vector<std::wstring> ChoiceMasks() const;
    // One file's entry of ChosenPriorities, so a page of files reads only its
    // own. `masks` is ChoiceMasks.
    lt::download_priority_t ChosenPriority(lt::file_storage const& files, lt::file_index_t index,
        std::vector<std::wstring> const& masks) const;
    // The priorities the Files view shows: libtorrent's, or the saved choice
    // while it waits for busy files; nothing until libtorrent reports them.
    std::optional<std::vector<lt::download_priority_t>> ShownPriorities(Detail const& detail) const;
    std::vector<lt::announce_entry> const& Trackers() const;
    void ApplyIntent();
    // Clears libtorrent's error and upload mode, and the copies of them that
    // the engine holds.
    void ClearDiskError();
    // libtorrent sets piece priorities again from the file priorities each
    // time those change, so this runs again after each change settles.
    void PrioritizePieces(std::vector<lt::download_priority_t> const& priorities) const;
    void Checkpoint(bool force);
private:
    WorkQuery work_;
};

// Rules about torrent content that previews share with torrents.
// The hashes as hexadecimal text, v1 first.
std::vector<std::string> Hashes(lt::info_hash_t const& hashes);
std::string Name(std::string name, std::vector<std::string> const& hashes);
// The file-name masks of skip patterns, one pattern per line.
std::vector<std::wstring> SkipMasks(std::string const& patterns);
lt::download_priority_t DefaultPriority(lt::file_storage const& files, lt::file_index_t index,
    std::vector<std::wstring> const& masks);
std::vector<lt::download_priority_t> DefaultPriorities(lt::file_storage const& files, std::string const& patterns = {});
std::string ContentPath(lt::file_storage const& files, lt::file_index_t index, Layout layout);
// A large torrent's file list goes in pages, one `files` reply each, so
// neither side ever holds the whole list as one message. A page ends at
// `filePage` files or once its paths reach `filePageNames` bytes, which keeps
// a reply with a disk path beside each path well under the frame limit.
constexpr std::size_t filePage = 5000;
constexpr std::size_t filePageNames = 2 << 20;
// The first part of each path `visitPaths` gives, sorted and each once: the
// files and folders at the save folder that hold every file. A path that is
// already full is its own part.
std::vector<std::string> Tops(std::function<void(std::function<void(std::string)> const&)> const& visitPaths);
// The pieces a file covers, end exclusive.
struct PieceRange
{
    std::int64_t first = 0;
    std::int64_t end = 0;
};
PieceRange Pieces(lt::torrent_info const& metadata, lt::file_index_t index);
std::vector<std::string> Urls(std::vector<lt::announce_entry> const& trackers);
}
