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
    char const* Code() const;
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
        std::string destination;
        // How far the move got.
        MoveStage stage = MoveStage::Staged;
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
    // The latest state change, the cause of the file completions after it.
    std::optional<State> origin_;
};

// A torrent in the list: its libtorrent handle, the facts the document saves
// about it, and what the engine knows about its errors and checkpoints.
struct Torrent
{
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
    QueueReport queueReport = QueueReport::Reported;
    SpeedHistory history;
    CheckpointPhase checkpointPhase = CheckpointPhase::Idle;
    std::optional<lt::add_torrent_params> pendingCheckpoint;
    bool unsaved = true;
    std::chrono::steady_clock::time_point retryAt{};
    std::int64_t savedUploaded = 0;
    std::optional<Problem> checkpointError;
    std::optional<Problem> hashError;
    std::string conflict;
    std::string diskError;
    std::string notifiedError;
    NamePhase namePhase = NamePhase::Pending;
    // Verification and file choices that wait until the files are not busy.
    bool needsRecheck = false;
    bool prioritiesPending = false;
    // The folder of a move the person asked for, which waits for busy files.
    std::optional<std::string> waitingMove;
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
    std::function<void(Outcome)> priorityCompletion;
    bool piecesPending = false;
    // A move or the suffix rename is changing the torrent's file paths. The
    // two never overlap, because each waits until the other's files are free.
    bool moving = false;
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
    std::vector<std::filesystem::path> Paths(bool logical = false) const;
    std::optional<Problem> Error() const;
    std::optional<Problem> Diagnose() const;
    // Its interrupted move waits to continue on its own, so it is moving,
    // not a problem for the person.
    bool ResumesMove() const;
    // A move continues on its own or waits for busy files, with no problem
    // that delays it.
    bool MoveWaits() const;
    // The torrent's own files are being prepared, renamed or moved.
    bool FilesBusy() const;
    bool MoveBlocked() const;
    // The engine keeps it paused for its own work, whatever the person chose:
    // a conflict, a move, the write before a rename, or names that wait for
    // preparation once it has `hasMetadata`.
    bool IsHeld(bool hasMetadata) const;
    Status Classify(bool sessionPaused) const;
    // The status without a move that runs or waits.
    Status ClassifyTransfer(bool sessionPaused) const;
    bool IsChanged() const;
    // Takes the newest status, and remembers when it shows new payload.
    void Update(lt::torrent_status latest);
    // The torrent's facts with its General and Files detail.
    Json Describe(Detail const& detail, std::shared_ptr<lt::torrent_info const> const& metadata) const;
    Json Describe(TorrentView view, bool includeFiles, Detail const& detail,
        std::shared_ptr<lt::torrent_info const> const& metadata,
        std::chrono::steady_clock::time_point since = {}) const;
    // `queue` is the torrent's place in the download queue, or -1 outside it.
    // `moveProgress` replaces the progress while the torrent's files move.
    Json Row(bool sessionPaused, int queue, std::optional<double> moveProgress) const;
    std::vector<lt::download_priority_t> ChosenPriorities(
        std::shared_ptr<lt::torrent_info const> const& metadata) const;
    std::vector<lt::announce_entry> const& Trackers() const;
    void ApplyIntent();
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
