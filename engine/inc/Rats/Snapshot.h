#pragma once

#include <nlohmann/json.hpp>
#include <cstdint>
#include <memory>
#include <optional>
#include <span>
#include <string>
#include <string_view>
#include <variant>
#include <vector>

namespace tt::rats
{
struct File
{
    std::string path;
    std::int64_t size = 0;
};

// One torrent as a Rats peer describes it. Every field is the peer's claim,
// not checked against the torrent's metadata.
struct Record
{
    // 40 lowercase hex characters.
    std::string hash;
    std::string title;
    std::int64_t size = 0;
    // In the peer's order, which is not the torrent's file index order.
    std::vector<File> files;
    std::optional<std::int64_t> seeders;
    std::optional<std::int64_t> leechers;
    // When the peer last scraped trackers, in Unix seconds.
    std::optional<std::int64_t> checked;

    // Nothing when a required field is missing, malformed or past its bound.
    // The title and paths are cleaned as libtorrent cleans a torrent's name
    // and paths.
    static std::optional<Record> Parse(nlohmann::json const& object);
};

// Parses JSON that a peer sent. Nothing when the text is malformed or nested
// deeper than any Rats message is, because the parser's memory grows with the
// nesting.
std::optional<nlohmann::json> ParseJson(std::string_view text);

// The integer member `key` of a peer's JSON object. Nothing when the member is
// missing, is not a whole number, or does not fit in 64 bits.
std::optional<std::int64_t> ReadInteger(nlohmann::json const& object, char const* key);

// Reads a `.ratsdb` snapshot while it arrives, without holding the file.
// Read throws std::runtime_error when the bytes are not a snapshot: wrong
// magic, unsupported version, or a length no Rats writer produces. The rest of
// a frame that does not inflate within its bound, and a record that fails
// validation, are skipped, so the rest of the file stays readable. A file that
// ends before its footer keeps every complete record, and Complete stays false.
class Snapshot
{
public:
    Snapshot();
    ~Snapshot();
    Snapshot(Snapshot const&) = delete;
    Snapshot& operator=(Snapshot const&) = delete;

    // Keeps the bytes for Read; decodes nothing.
    void Append(std::span<char const> bytes);
    // Decodes kept bytes until they run out or a few megabytes of text and a
    // few thousand records are decoded, so one call stays short however well
    // the peer's data compresses. Pending says whether more is ready.
    std::vector<Record> Read();
    // True when Read stopped at its bound with bytes left to decode.
    bool Pending() const;
    // How many appended bytes Read has finished with, from the file's start.
    std::int64_t Consumed() const;
    // True once the footer is read.
    bool Complete() const;

private:
    struct Head
    {
    };
    struct Frames
    {
    };
    struct Tail
    {
    };
    struct Done
    {
    };

    // The frame being inflated.
    struct Frame;

    bool Step();
    bool Inflate(std::vector<Record>& records, std::size_t& text);
    void Split(std::string_view text, std::vector<Record>& records);
    void Skip(std::size_t count);

    std::vector<std::uint8_t> buffer_;
    std::size_t offset_ = 0;
    std::int64_t consumed_ = 0;
    bool pending_ = false;
    std::unique_ptr<Frame> frame_;
    std::variant<Head, Frames, Tail, Done> stage_;
};
}
