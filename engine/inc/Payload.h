#pragma once

#include "Enums.h"
#include <libtorrent/bitfield.hpp>
#include <libtorrent/torrent_info.hpp>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <expected>
#include <filesystem>
#include <functional>
#include <memory>
#include <mutex>
#include <optional>
#include <stop_token>
#include <string>
#include <vector>

// The disk work on torrent files: moving them between folders and deleting
// them permanently. Callers decide which files; this module decides how.
namespace tt::Payload
{
struct File
{
    std::filesystem::path source;
    std::filesystem::path destination;
    // How many bytes at the start of the unfinished copy at its `Partial`
    // name a flush proved on disk; 0 when nothing is proved.
    std::uint64_t proven = 0;
    bool existing = false;
};

struct Pieces
{
    std::shared_ptr<libtorrent::torrent_info const> metadata;
    std::vector<std::vector<libtorrent::sha256_hash>> hashes;
    // Libtorrent's storage reader includes the bytes of skipped files that
    // it keeps in its part file instead of their visible paths.
    std::function<std::optional<std::vector<std::byte>>(libtorrent::piece_index_t)> read;
};

struct Reconciled
{
    std::optional<libtorrent::typed_bitfield<libtorrent::piece_index_t>> pieces;
};

// The bytes at the start of a copy that a flush put on the disk: the copy's
// final path and their count.
struct Proof
{
    std::filesystem::path destination;
    std::uint64_t bytes = 0;

    bool operator==(Proof const&) const = default;
};

// The bytes of one move. Its worker writes them while the engine reads them.
// A copy that keeps a sparse file's holes counts only the bytes that hold
// data, because it skips the holes at once.
struct Progress
{
    std::atomic<std::uint64_t> total{0};
    std::atomic<std::uint64_t> done{0};
    // The part of `done` that was copied. A rename moves no bytes, so the
    // copy rate leaves it out.
    std::atomic<std::uint64_t> copied{0};
    // The bytes that continuing an unfinished copy compared with the source.
    // They change while `done` may not, and the stall rule counts them.
    std::atomic<std::uint64_t> compared{0};
    // Set by the worker once libtorrent released the files and their
    // placement begins, which starts the engine's stall clock.
    std::atomic<bool> placing{false};
    // Set by the engine to cancel the placement in progress.
    std::atomic<MoveCancel> cancel{MoveCancel::None};
    // Set by the worker when a cancel that keeps the copies finds a copy
    // that CopyFile2 writes. CopyFile2 deletes the file it cancels, so the
    // copy instead waits in its worker until the process ends, which leaves
    // the file and its proof to continue from.
    std::atomic<bool> parked{false};

    void Prove(Proof proof)
    {
        std::lock_guard lock(gate_);
        proof_ = std::move(proof);
    }
    // The latest proof of the copy that the worker writes or wrote last.
    std::optional<Proof> Proven() const
    {
        std::lock_guard lock(gate_);
        return proof_;
    }

private:
    mutable std::mutex gate_;
    std::optional<Proof> proof_;
};

struct Failure
{
    MoveFailure kind;
    std::string detail;
};

// Puts each file at its destination: a rename on one volume, otherwise a copy
// that keeps its source, which the caller deletes once the new folder is
// saved. A copy has its `Partial` name until it is complete, so an
// interruption never leaves part of a file at the destination's name. A copy
// proves its bytes on disk every gibibyte, so a later move continues an
// unfinished copy from its proof, after it compared the bytes before the
// proof with the source; without a proof that holds, it compares the whole
// unfinished copy with the source and copies only the blocks that differ. A
// missing source is skipped, unless an unfinished copy of it exists, which
// fails the move. Before it copies, it refuses a volume without the space its
// copies need. Each complete copy is flushed before it takes its final name,
// so the caller may delete its source once the new folder is saved. On
// failure it deletes its copies and renames files back, except after a cancel
// that keeps them, which leaves the complete copies, the renamed files and
// the unfinished copy where they are and reports Split. It blocks until every
// file is placed, restored or kept, or forever for a parked copy.
std::expected<void, Failure> Move(std::vector<File> const& files, Progress& progress);

// Files follow the metadata's non-padding file order. Existing destinations
// are verified until the first failed piece; thereafter only verified source
// pieces replace them. The remaining destination pieces are checked last;
// the returned map describes the resulting files without another full check.
std::expected<Reconciled, Failure> Reconcile(Pieces const& pieces, std::vector<File> const& files,
    Progress& progress);

// Deletes the files permanently on parallel workers, and throws the first
// failure after trying every file. A file that is already gone counts as
// deleted. Once `stop` is requested, it deletes no further file.
void Delete(std::vector<std::filesystem::path> const& files, std::stop_token stop = {});

// The name of the copy to `destination` while `Move` writes it. A file with
// this name is a copy that a move left unfinished, which a later move
// continues; see `Move`.
std::filesystem::path Partial(std::filesystem::path const& destination);

// Whether the destination is a complete copy that `Move` made of the
// unchanged source.
bool IsCopy(File const& file);
}
