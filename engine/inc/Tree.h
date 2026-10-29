#pragma once

#include "Engine.h"
#include <libtorrent/download_priority.hpp>
#include <libtorrent/torrent_info.hpp>
#include <optional>
#include <string>
#include <string_view>
#include <unordered_map>
#include <variant>
#include <vector>

namespace tt
{
// The rows a file browser holds, and the most entries one `tree` reply
// carries; `filePageNames` bounds the bytes of their names and, for a
// torrent, of its files' paths on disk.
constexpr std::size_t fileWindow = 10'000;

// The words of a search query. Text matches when each word starts a word of
// it, in any order, as the search ruling in AGENTS.md requires: "50 para"
// matches "Paracetamol 500mg", and "para" does not match "comparable". The
// app's FileName.SearchWords and StartsWord implement the same rule, and both
// checks hold its examples.
class Search
{
public:
    // Throws std::invalid_argument for a query without words or with more
    // than 64.
    explicit Search(std::string_view query);
    // The words that start a word of `text`, which Normalize gave, as bits
    // by the words' order.
    std::uint64_t Found(std::wstring_view text) const;
    // Every word's bit.
    std::uint64_t All() const;
    // `text` as search compares it: without its accents, in lower case.
    static std::wstring Normalize(std::string_view text);

private:
    std::vector<std::wstring> words_;
};

// What the tree worker reads of a preview's or a torrent's files: the
// immutable metadata and copies of the values that change, so the worker
// never touches the engine's state.
struct Listing
{
    std::shared_ptr<lt::torrent_info const> metadata;
    Layout layout = Layout::Keep;
    // One per file.
    std::vector<lt::download_priority_t> priorities;
    // A torrent's downloaded bytes per file; nothing until libtorrent
    // reports them, and always nothing for a preview.
    std::optional<std::vector<std::int64_t>> progress;
    // A torrent's replies carry downloaded bytes and each file's path on
    // disk; a preview's carry neither. A path on disk is about as long as the
    // file's path in the torrent, which then counts toward `filePageNames`
    // with its name.
    bool torrent = false;
};

// A preview's or a torrent's folders and files, built from a Listing for one
// request on the tree worker.
class Tree
{
public:
    struct Folder
    {
        std::string path;
    };
    // What a `tree` request reads, and what a file choice covers.
    using Target = std::variant<lt::file_index_t, Folder, Search>;

    explicit Tree(Listing listing);
    // The `tree` reply for `target`, whose file objects carry no disk_path
    // yet. Nothing when it names no folder, no file or a padding file.
    std::optional<Json> Describe(Target const& target) const;
    // The files a folder or query `target` covers; a file choice needs no
    // tree. Nothing when it names no folder.
    std::optional<std::vector<lt::file_index_t>> Files(Target const& target) const;
    // `path` and the folder values of each of `paths` that names a folder.
    Json Values(std::vector<std::string> const& paths) const;
    // The whole torrent's wanted_count and wanted_size.
    Json Totals() const;

private:
    struct Node
    {
        std::string name;
        int parent = -1;
        std::vector<int> folders;
        std::vector<lt::file_index_t> files;
        // The bytes that its direct entries' names, and its files' paths on
        // disk, add to a reply.
        std::size_t names = 0;
        std::int64_t count = 0;
        std::int64_t size = 0;
        std::int64_t wantedCount = 0;
        std::int64_t wantedSize = 0;
        std::int64_t downloaded = 0;
        lt::download_priority_t priority{};
        bool mixed = false;
    };
    // The entries a reply holds: the folders whose direct entries it lists
    // whole, and for a query its matches and the folders on their paths.
    struct Shown
    {
        std::vector<bool> open;
        std::unordered_map<int, std::vector<int>> folders;
        std::unordered_map<int, std::vector<lt::file_index_t>> files;
        std::vector<bool> matches;
        std::size_t entries = 0;
        std::size_t names = 0;
    };
    // A query's matches: for each folder, the words its path holds, and the
    // highest folder on its path that matches, or -1.
    struct Matches
    {
        std::vector<std::uint64_t> words;
        std::vector<int> top;
    };

    // The folder at `path`, created with the folders on its path.
    int Ensure(std::string_view path);
    std::optional<int> Find(std::string const& path) const;
    std::string_view Name(lt::file_index_t file) const;
    bool TooMany(int folder) const;
    // Opens `folders` and then each deeper level of their subtrees whole,
    // while the entries fit.
    void Open(std::vector<int> folders, Shown& shown) const;
    Matches Match(Search const& search) const;
    bool Matched(Search const& search, Matches const& matches, lt::file_index_t file) const;
    Json Describe(int folder, Shown const& shown) const;
    Json DescribeFile(lt::file_index_t file, bool match) const;
    void Values(Node const& node, Json& value) const;
    void Collect(int folder, std::vector<lt::file_index_t>& files) const;

    Listing listing_;
    std::vector<Node> nodes_;
    std::unordered_map<std::string, int> paths_;
    // The folder of each file; -1 for padding.
    std::vector<int> folderOf_;
};
}
