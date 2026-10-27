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
#include <future>
#include <map>
#include <optional>
#include <set>
#include <string>
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

// Reads file priorities as the add command sends them. Throws when a value is
// not a whole number from 0 to 255.
std::vector<lt::download_priority_t> ReadPriorities(Json const& values);

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

// A torrent in the list: its libtorrent handle, the facts the document saves
// about it, and what the engine knows about its errors and checkpoints.
struct Torrent
{
    struct Recovery
    {
        DiskPhase phase = DiskPhase::Releasing;
        std::shared_future<void> released;
        lt::error_code cause;
        bool retryRequested = false;
        // Whether the last probe found the folder not answering.
        bool absent = false;
        // The files whose data is gone and whose pieces a reload drops.
        // Until it has, each checkpoint drops them too, so a start after a
        // cut downloads only those files again instead of checking every one.
        std::vector<lt::file_index_t> lost;
    };

    std::string torrentId;
    lt::torrent_handle handle;
    Facts facts;
    // A conflicting restored identity keeps its parameters until its other
    // owner is removed; it must never borrow that owner's live handle.
    std::optional<lt::add_torrent_params> restore;
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
    std::shared_ptr<Recovery> recovery;
    StorageRetry storageRetry = StorageRetry::Automatic;
    // How far the torrent had got when the engine last continued it, once a
    // torrent that the engine reloaded has read its resume data. A storage
    // error after it got further is a new one, which gets its own retry, so
    // an error that returns at once and one that takes Windows minutes to
    // report are judged alike.
    std::optional<FileProgress> retriedProgress;
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
    // A stalled check keeps its reason until storage recovery permits retry.
    std::optional<Problem> checkError;
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
    // A move or the suffix rename is changing the torrent's file paths. The
    // two never overlap, because each waits until the other's files are free.
    bool moving = false;
    std::optional<Problem> moveError;
    // How the engine continues a failed move to the final folder. It never
    // uses Retried: the first failure waits for the destination to answer.
    StorageRetry moveRetry = StorageRetry::Automatic;
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
    std::vector<std::filesystem::path> Paths(bool logical = false) const;
    std::optional<Problem> Error() const;
    std::optional<Problem> StorageProblem() const;
    // A storage problem or a failed move to the final folder that the engine
    // no longer retries by itself, so it waits for the person's Retry.
    bool NeedsPerson() const;
    // Whether a storage failure stopped the torrent and its problem remains,
    // so storage recovery leaves it to CheckStopped or the person's Retry.
    // Other work can clear the problem, such as names that are chosen at
    // last, and the torrent then runs again.
    bool StorageStopped() const;
    std::optional<Problem> Diagnose() const;
    // A move waits for its turn, or for the person's answer about the work
    // that the last end left unfinished, with no problem that delays it.
    bool MoveWaits() const;
    // The torrent's own files are being prepared, renamed or moved.
    bool FilesBusy() const;
    bool MoveBlocked() const;
    // The person's Start, Stop, Verify or Retry answers a move problem. One
    // that left no interrupted move clears, because its files are where they
    // were, and the engine retries the move's next failure by itself again.
    void AnswerMoveProblem();
    // The person's Start, Force, Retry or Verify answers a storage problem:
    // a stalled check's error clears, and the engine retries the next storage
    // failure by itself again.
    void AnswerStorageProblem();
    // The person's Start, Force or Retry clears every problem that stopped
    // the torrent. The caller applies the intent.
    void Retry();
    // The engine keeps it paused for its own work, whatever the person chose:
    // a conflict, a move, the write before a rename, a check that waits for
    // its drive, for the person's answer or stalled, storage that stopped it,
    // or names that wait for preparation once it has `hasMetadata`.
    bool IsHeld(bool hasMetadata) const;
    // The engine holds its check, because another job uses its drive.
    bool CheckWaits() const;
    // Its current file names are read, or it has no metadata to name files.
    bool NamesKnown() const;
    // libtorrent checks its files now, as the last status reports.
    bool ChecksFiles(bool sessionPaused) const;
    Status Classify(bool sessionPaused) const;
    // The status without a move that runs or waits.
    Status ClassifyTransfer(bool sessionPaused) const;
    bool IsChanged() const;
    // Takes the newest status, and remembers when it shows new payload.
    void Update(lt::torrent_status latest, lt::time_point at);
    // The torrent's facts with its General and Files detail.
    Json Describe(Detail const& detail, std::shared_ptr<lt::torrent_info const> const& metadata) const;
    Json Describe(TorrentView view, bool includeFiles, Detail const& detail,
        std::shared_ptr<lt::torrent_info const> const& metadata,
        std::chrono::steady_clock::time_point since = {}) const;
    // `queue` is the torrent's place in the download queue, or -1 outside it.
    // `moveProgress` replaces the progress while the torrent's files move.
    Json Row(bool sessionPaused, int queue, std::optional<double> moveProgress, DriveWait const& wait) const;
    std::vector<lt::download_priority_t> ChosenPriorities(
        std::shared_ptr<lt::torrent_info const> const& metadata) const;
    std::vector<lt::announce_entry> const& Trackers() const;
    void ApplyIntent();
    void ClearDiskError();
    // libtorrent sets piece priorities again from the file priorities each
    // time those change, so this runs again after each change settles.
    void PrioritizePieces(std::vector<lt::download_priority_t> const& priorities) const;
    void Checkpoint(bool force);
};

// Rules about torrent content that previews share with torrents.
// The hashes as hexadecimal text, v1 first.
std::vector<std::string> Hashes(lt::info_hash_t const& hashes);
std::string Name(std::string name, std::vector<std::string> const& hashes);
lt::download_priority_t DefaultPriority(lt::file_storage const& files, lt::file_index_t index);
std::vector<lt::download_priority_t> DefaultPriorities(lt::file_storage const& files, std::string const& patterns = {});
std::string ContentPath(lt::file_storage const& files, lt::file_index_t index, Layout layout);
Json Files(std::shared_ptr<lt::torrent_info const> const& metadata);
std::vector<std::string> Urls(std::vector<lt::announce_entry> const& trackers);
}
