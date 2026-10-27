#pragma once

#include "Enums.h"
#include <libtorrent/add_torrent_params.hpp>
#include <cstdint>
#include <filesystem>
#include <memory>
#include <optional>
#include <string>
#include <vector>

namespace tt
{
// All operations run on the index's serialized Store worker, never the engine
// state thread. External identities are verified full hashes, not SQLite keys.
class Catalogue
{
public:
    struct Text
    {
        std::string value;
        bool exclude = false;
    };
    struct Range
    {
        std::optional<std::int64_t> minimum;
        std::optional<std::int64_t> maximum;
    };
    struct Rule
    {
        std::string ruleId;
        std::string name;
        bool enabled = true;
        Text title;
        Range size;
        Range count;
        FileQuantifier quantifier = FileQuantifier::Any;
        Text filename;
        Text path;
        Text extension;
        Range fileSize;
    };
    struct Settings
    {
        std::int64_t budget = 1024LL * 1024 * 1024;
        int staleHours = 4320;
        std::vector<Rule> rules;
    };
    struct Observation
    {
        std::optional<std::int64_t> seeds;
        std::optional<std::int64_t> peers;
        std::int64_t observed = 0;
        int responses = 0;
    };
    struct Observed
    {
        std::string hash;
        Observation observation;
    };
    struct File
    {
        int index = 0;
        std::string name;
        std::string path;
        std::int64_t size = 0;
        bool padding = false;
        bool symlink = false;
        std::string root;
    };
    struct Source
    {
        std::string hash;
        std::optional<std::string> v1;
        std::optional<std::string> v2;
        std::string name;
        std::int64_t size = 0;
        int count = 0;
        std::int64_t admitted = 0;
        std::optional<std::int64_t> seen;
        Observation availability;
        File file;
        // False for a claimed entry: its name, sizes and files are a peer's
        // description, and its file indices are listing positions.
        bool verified = false;
    };
    // A torrent a peer describes without its metadata. Nothing in it is
    // verified, and the order of its files is not a torrent file index.
    struct Claim
    {
        struct File
        {
            std::string path;
            std::int64_t size = 0;
        };
        std::string hash;
        std::string name;
        std::int64_t size = 0;
        std::vector<File> files;
        // Kept only when observed is the peer's own scrape time, because the
        // time a claim arrives says nothing about the swarm.
        Observation availability;
    };
    struct Receipt
    {
        // Claims handled, stored or skipped; the caller submits the rest again.
        int next = 0;
        int stored = 0;
        // The catalogue reached its storage budget, so nothing more is stored.
        bool full = false;
    };
    // When a Rats peer's snapshot was last requested, in Unix seconds.
    struct Sync
    {
        std::string peerId;
        std::int64_t requested = 0;
    };
    struct Entry
    {
        Source torrent;
        bool isPrivate = true;
        std::vector<File> files;
        std::string comment;
        std::string creator;
        std::int64_t created = 0;
        int offset = 0;
        int next = 0;
        int selectableCount = 0;
        bool more = false;
    };
    struct SourcePage
    {
        std::vector<Source> sources;
        std::vector<std::string> aliases;
        int offset = 0;
        int next = 0;
        bool more = false;
        bool limited = false;
    };
    struct Hit
    {
        Source matched;
        Source preferred;
        std::vector<Source> sources;
        std::vector<std::string> aliases;
        bool sourcesLimited = false;
    };
    struct Cursor
    {
        std::int64_t entry = 0;
        int file = -1;
        std::int64_t watermark = 0;
        std::int64_t generation = 0;
        std::string value;
    };
    struct Query
    {
        CatalogueTarget target = CatalogueTarget::Files;
        CatalogueSort sort = CatalogueSort::Name;
        bool descending = false;
        std::string text;
        std::string extension;
        Range size;
        std::string torrent;
        int limit = 100;
        std::optional<Cursor> cursor;
    };
    struct Page
    {
        std::vector<Hit> hits;
        std::optional<Cursor> next;
        bool limited = false;
        bool exhaustive = false;
        std::int64_t visited = 0;
        std::int64_t watermark = 0;
        std::int64_t generation = 0;
    };
    struct Status
    {
        bool exists = false;
        std::int64_t torrents = 0;
        std::int64_t files = 0;
        std::int64_t rejected = 0;
        std::int64_t pending = 0;
        std::int64_t ready = 0;
        std::int64_t nodes = 0;
        std::int64_t allocated = 0;
        std::int64_t reusable = 0;
        bool rebuilding = false;
        bool projecting = false;
        std::int64_t rebuilt = 0;
        std::int64_t generation = 0;
    };
    struct Progress
    {
        bool rebuilding = false;
        bool projecting = false;
        int published = 0;
    };
    struct Endpoint
    {
        std::string address;
        int port = 0;
    };
    struct Pending
    {
        std::string lookup;
        std::optional<Endpoint> source;
    };
    struct Node
    {
        std::string address;
        int port = 0;
        std::string nodeId;
        std::int64_t eligible = 0;
    };
    struct Selection
    {
        std::vector<Pending> missing;
        std::vector<Pending> scrape;
    };
    struct Admission
    {
        bool accepted = false;
        std::string hash;
        std::vector<std::string> reasons;
    };
    struct Removal
    {
        std::string token;
        std::int64_t torrents = 0;
        std::int64_t files = 0;
        std::int64_t bytes = 0;
        std::int64_t removed = 0;
        bool more = false;
    };
    struct PolicyPreview
    {
        std::int64_t suppressed = 0;
        std::int64_t retained = 0;
        std::int64_t released = 0;
    };
    struct RemovalRow
    {
        Source torrent;
        std::vector<std::string> reasons;
    };
    struct RemovalPage
    {
        std::vector<RemovalRow> entries;
        std::int64_t next = 0;
        bool more = false;
    };

    explicit Catalogue(std::filesystem::path directory);
    ~Catalogue();
    Catalogue(Catalogue const&) = delete;
    Catalogue& operator=(Catalogue const&) = delete;

    void Open(bool create);
    Status ReadStatus();
    CatalogueCondition ReadCondition() const noexcept;
    PolicyPreview PreviewPolicy(Settings const& settings);
    void Configure(Settings settings);
    Admission Admit(libtorrent::add_torrent_params const& params,
        std::string const& lookup = {}, std::vector<char> const& metainfo = {},
        bool discovered = false);
    // Stores claims as unverified entries, searchable at once. Skips a claim
    // whose hash is not 40 lowercase hex digits, whose sizes are negative, or
    // whose hash the catalogue holds or has rejected. Skips a claim an
    // exclusion rule matches without remembering it, so a claim never bans
    // the real torrent.
    // Works for the store worker's time slice, but always handles one claim.
    Receipt Receive(std::vector<Claim> const& claims);
    std::optional<Sync> ReadSync(std::string const& peerId);
    void SaveSync(Sync const& sync);
    void Discover(std::vector<std::string> const& lookups, std::int64_t now,
        std::optional<Endpoint> const& source = {});
    void Discover(std::vector<Pending> const& candidates, std::int64_t now);
    Selection Triage(std::vector<Pending> const& candidates, std::int64_t now,
        std::int64_t rescrapeSeconds);
    void SaveNodes(std::vector<Node> const& nodes);
    std::vector<Node> Nodes(int limit, std::int64_t now);
    Page Search(Query const& query);
    Entry Detail(std::string const& hash, int offset = 0, int limit = 200,
        std::string const& query = {});
    SourcePage Sources(Source const& source, Query const& query, int offset = 0, int limit = 100);
    // Fails for an unverified entry, which has no metadata to add from.
    libtorrent::add_torrent_params Metadata(std::string const& hash, Query const& scope);
    // Saves a batch in one transaction. Newer saved observations win.
    void Observe(std::vector<Observed> const& observations);
    Removal Preview(CatalogueRemoval kind);
    RemovalPage PreviewRows(std::string const& token, std::int64_t after = 0, int limit = 100);
    Removal Remove(std::string const& token, int limit = 32);
    Progress Rebuild(int limit = 8);
    Progress ProjectPending(int limit = 1024);
    void CancelRebuild();
    int Evict(std::int64_t now, int limit = 32);
    void Interrupt();

private:
    class Impl;
    std::unique_ptr<Impl> impl_;
};
}
