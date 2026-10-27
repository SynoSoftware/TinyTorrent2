// Reads the Rats Search 2.3.5 record and snapshot formats, copyright (c)
// 2017-2018 Alexey Kasyanchuk (MIT); distributed notice:
// licenses/RatsSearch/RatsSearch.txt.
#include "Rats/Snapshot.h"
#include <libtorrent/torrent_info.hpp>
#include <boost/beast/zlib/inflate_stream.hpp>
#include <algorithm>
#include <cmath>
#include <limits>
#include <stdexcept>
#include <utility>

namespace tt::rats
{
namespace
{
using View = std::span<std::uint8_t const>;

// Titles and paths are shown in lists, and Windows limits a path to far less.
constexpr std::size_t maxText = 1024;
// A torrent with more files is skipped, so one record cannot fill the catalogue.
constexpr std::size_t maxFiles = 50000;
// A record's JSON text above this is skipped before it is parsed.
constexpr std::size_t maxRecord = 16u << 20;
// Records nest three levels (record, file list, file); `info` adds a few.
constexpr std::size_t maxDepth = 12;
// Rats' writer refuses a frame above this, and its reader rejects one.
constexpr std::size_t maxFrame = 64u << 20;
// One frame holds about 500 records, which expand to a few megabytes.
constexpr std::size_t maxExpanded = 64u << 20;
constexpr std::size_t maxHeader = 1u << 20;
constexpr std::size_t inflateStep = 64u << 10;
// The most that one Read decodes, so a catalogue job stays short and the
// worker, which also serves Search, is free between Reads.
constexpr std::size_t readText = 4u << 20;
constexpr std::size_t readRecords = 2000;
// A Qt qCompress frame starts with a 4-byte size hint, then a zlib stream
// with its 2-byte header.
constexpr std::size_t framePrefix = 4 + 2;
constexpr std::string_view magic("RATSDB", 7);

std::size_t Be32(View bytes)
{
    return (std::size_t{bytes[0]} << 24) | (std::size_t{bytes[1]} << 16) | (std::size_t{bytes[2]} << 8) |
        std::size_t{bytes[3]};
}

bool Shallow(std::string_view text)
{
    std::size_t depth = 0;
    bool quoted = false;
    bool escaped = false;
    for (char c : text)
    {
        if (quoted)
        {
            if (escaped)
            {
                escaped = false;
            }
            else if (c == '\\')
            {
                escaped = true;
            }
            else if (c == '"')
            {
                quoted = false;
            }
        }
        else if (c == '"')
        {
            quoted = true;
        }
        else if (c == '[' || c == '{')
        {
            if (++depth > maxDepth)
            {
                return false;
            }
        }
        else if ((c == ']' || c == '}') && depth > 0)
        {
            --depth;
        }
    }
    return true;
}

// Cleans a title the way libtorrent cleans a torrent's name from the DHT:
// invalid UTF-8, control characters and characters Windows refuses in a file
// name become '_', and invisible or direction-changing characters go, so a
// claim cannot disguise one name as another. Nothing when nothing is left.
std::optional<std::string> Title(std::string const& text)
{
    if (text.size() > maxText)
    {
        return std::nullopt;
    }
    std::string clean;
    libtorrent::aux::sanitize_append_path_element(clean, {text.data(), text.size()});
    if (clean.empty())
    {
        return std::nullopt;
    }
    return clean;
}

// Cleans each element of a path the way libtorrent cleans a torrent's file
// paths, and joins them with '/' as the catalogue stores paths.
std::optional<std::string> Path(std::string const& text)
{
    if (text.size() > maxText)
    {
        return std::nullopt;
    }
    std::string clean;
    std::string_view rest(text);
    while (!rest.empty())
    {
        auto end = rest.find_first_of("/\\");
        auto element = rest.substr(0, end);
        rest.remove_prefix(end == std::string_view::npos ? rest.size() : end + 1);
        if (!element.empty())
        {
            libtorrent::aux::sanitize_append_path_element(clean, {element.data(), element.size()}, true);
        }
    }
    if (clean.empty())
    {
        return std::nullopt;
    }
    std::ranges::replace(clean, '\\', '/');
    return clean;
}

std::string const* ReadText(nlohmann::json const& object, char const* key)
{
    auto member = object.find(key);
    if (member == object.end() || !member->is_string())
    {
        return nullptr;
    }
    return member->get_ptr<std::string const*>();
}

std::optional<std::int64_t> ReadCount(nlohmann::json const& object, char const* key)
{
    auto value = ReadInteger(object, key);
    if (value && *value < 0)
    {
        return std::nullopt;
    }
    return value;
}
}

std::optional<nlohmann::json> ParseJson(std::string_view text)
{
    if (!Shallow(text))
    {
        return std::nullopt;
    }
    auto json = nlohmann::json::parse(text.data(), text.data() + text.size(), nullptr, false);
    if (json.is_discarded())
    {
        return std::nullopt;
    }
    return json;
}

std::optional<std::int64_t> ReadInteger(nlohmann::json const& object, char const* key)
{
    auto member = object.find(key);
    if (member == object.end())
    {
        return std::nullopt;
    }
    if (member->is_number_unsigned())
    {
        auto value = member->get<std::uint64_t>();
        if (value > static_cast<std::uint64_t>(std::numeric_limits<std::int64_t>::max()))
        {
            return std::nullopt;
        }
        return static_cast<std::int64_t>(value);
    }
    if (member->is_number_integer())
    {
        return member->get<std::int64_t>();
    }
    if (member->is_number_float())
    {
        // Qt writes a whole number above 2^31 as a double.
        auto value = member->get<double>();
        if (std::trunc(value) == value && std::fabs(value) < 9.2e18)
        {
            return static_cast<std::int64_t>(value);
        }
    }
    return std::nullopt;
}

std::optional<Record> Record::Parse(nlohmann::json const& object)
{
    if (!object.is_object())
    {
        return std::nullopt;
    }
    Record record;
    auto hash = ReadText(object, "hash");
    if (!hash || hash->size() != 40)
    {
        return std::nullopt;
    }
    record.hash = *hash;
    for (char& c : record.hash)
    {
        if (c >= 'A' && c <= 'F')
        {
            c = static_cast<char>(c - 'A' + 'a');
        }
        else if ((c < '0' || c > '9') && (c < 'a' || c > 'f'))
        {
            return std::nullopt;
        }
    }
    auto name = ReadText(object, "name");
    auto title = name ? Title(*name) : std::nullopt;
    auto size = ReadInteger(object, "size");
    if (!title || !size || *size < 0)
    {
        return std::nullopt;
    }
    record.title = std::move(*title);
    record.size = *size;
    record.seeders = ReadCount(object, "seeders");
    record.leechers = ReadCount(object, "leechers");
    auto checked = ReadInteger(object, "trackersChecked");
    if (checked && *checked > 0)
    {
        record.checked = *checked / 1000;
    }
    auto list = object.find("files_list");
    if (list != object.end())
    {
        if (!list->is_array() || list->size() > maxFiles)
        {
            return std::nullopt;
        }
        record.files.reserve(list->size());
        for (auto const& item : *list)
        {
            auto text = ReadText(item, "path");
            auto path = text ? Path(*text) : std::nullopt;
            auto fileSize = ReadInteger(item, "size");
            if (!path || !fileSize || *fileSize < 0)
            {
                return std::nullopt;
            }
            record.files.push_back({std::move(*path), *fileSize});
        }
    }
    return record;
}

struct Snapshot::Frame
{
    boost::beast::zlib::inflate_stream inflater;
    std::string output = std::string(inflateStep, '\0');
    // Bytes of the frame not yet consumed.
    std::size_t left = 0;
    std::size_t expanded = 0;
    // The zlib header is checked.
    bool started = false;
    // The inflater gives no more output: no frame is open, or its stream
    // ended, failed or passed maxExpanded and the rest of it is skipped.
    bool ended = true;
    // The record line so far.
    std::string line;
    // The line is past maxRecord, so it is dropped up to its end.
    bool oversized = false;
};

Snapshot::Snapshot() : frame_(std::make_unique<Frame>()) {}

Snapshot::~Snapshot() = default;

void Snapshot::Append(std::span<char const> bytes)
{
    if (Complete())
    {
        consumed_ += static_cast<std::int64_t>(bytes.size());
        return;
    }
    auto first = reinterpret_cast<std::uint8_t const*>(bytes.data());
    buffer_.insert(buffer_.end(), first, first + bytes.size());
}

std::vector<Record> Snapshot::Read()
{
    std::vector<Record> records;
    std::size_t text = 0;
    pending_ = false;
    // Between frames Step reads the next part of the file; inside a frame
    // Inflate does.
    while (frame_->ended && frame_->left == 0 ? Step() : Inflate(records, text))
    {
    }
    // Bytes after the footer belong to no frame.
    auto used = Complete() ? buffer_.size() : offset_;
    buffer_.erase(buffer_.begin(), buffer_.begin() + static_cast<std::ptrdiff_t>(used));
    consumed_ += static_cast<std::int64_t>(used);
    offset_ = 0;
    return records;
}

bool Snapshot::Pending() const
{
    return pending_;
}

std::int64_t Snapshot::Consumed() const
{
    return consumed_;
}

bool Snapshot::Complete() const
{
    return std::holds_alternative<Done>(stage_);
}

bool Snapshot::Step()
{
    View rest(buffer_.data() + offset_, buffer_.size() - offset_);
    if (std::holds_alternative<Head>(stage_))
    {
        // Magic, version, then the header's length.
        constexpr std::size_t fixed = 7 + 4 + 4;
        if (rest.size() < fixed)
        {
            return false;
        }
        if (!std::equal(magic.begin(), magic.end(), rest.begin()))
        {
            throw std::runtime_error("not a Rats snapshot");
        }
        if (Be32(rest.subspan(7)) != 1)
        {
            throw std::runtime_error("unsupported snapshot version");
        }
        auto header = Be32(rest.subspan(11));
        if (header > maxHeader)
        {
            throw std::runtime_error("snapshot header too large");
        }
        if (rest.size() < fixed + header)
        {
            return false;
        }
        offset_ += fixed + header;
        stage_ = Frames{};
        return true;
    }
    if (std::holds_alternative<Frames>(stage_))
    {
        if (rest.size() < 4)
        {
            return false;
        }
        auto length = Be32(rest);
        offset_ += 4;
        if (length == 0)
        {
            stage_ = Tail{};
            return true;
        }
        if (length > maxFrame)
        {
            throw std::runtime_error("snapshot frame too large");
        }
        auto& frame = *frame_;
        frame.left = length;
        frame.expanded = 0;
        frame.started = false;
        frame.ended = false;
        frame.line.clear();
        frame.oversized = false;
        return true;
    }
    if (std::holds_alternative<Tail>(stage_))
    {
        if (rest.size() < 4)
        {
            return false;
        }
        auto length = Be32(rest);
        if (length > maxHeader)
        {
            throw std::runtime_error("snapshot footer too large");
        }
        if (rest.size() < 4 + length)
        {
            return false;
        }
        offset_ += 4 + length;
        stage_ = Done{};
        return true;
    }
    return false;
}

// Inflates the current frame as its bytes arrive. The size hint is the
// peer's word, so the output grows only as the stream produces it, up to
// maxExpanded. Beast inflates raw deflate, so this checks the zlib header
// itself and does not read the Adler-32 trailer; the transfer's SHA-256
// covers the bytes. False when it needs more bytes or the Read's bound is
// reached.
bool Snapshot::Inflate(std::vector<Record>& records, std::size_t& text)
{
    auto& frame = *frame_;
    View rest(buffer_.data() + offset_, std::min(buffer_.size() - offset_, frame.left));
    if (!frame.started)
    {
        if (frame.left < framePrefix)
        {
            frame.ended = true;
        }
        else if (rest.size() < framePrefix)
        {
            return false;
        }
        else
        {
            unsigned method = rest[4];
            unsigned flags = rest[5];
            frame.ended = (method & 0x0f) != 8 || (method >> 4) > 7 || ((method << 8) | flags) % 31 != 0 ||
                (flags & 0x20) != 0;
            frame.inflater.reset();
            Skip(framePrefix);
        }
        frame.started = true;
        return true;
    }
    if (frame.ended)
    {
        Skip(rest.size());
        return frame.left == 0;
    }
    // With the whole frame given, the inflater may still hold output.
    if (rest.empty() && frame.left > 0)
    {
        return false;
    }
    if (text >= readText || records.size() >= readRecords)
    {
        pending_ = true;
        return false;
    }
    boost::beast::zlib::z_params params{};
    params.next_in = rest.data();
    params.avail_in = rest.size();
    params.next_out = frame.output.data();
    params.avail_out = frame.output.size();
    boost::beast::error_code error;
    frame.inflater.write(params, boost::beast::zlib::Flush::none, error);
    auto taken = rest.size() - params.avail_in;
    auto produced = frame.output.size() - params.avail_out;
    Skip(taken);
    text += produced;
    frame.expanded += produced;
    if (frame.expanded > maxExpanded)
    {
        frame.ended = true;
        frame.line.clear();
        return true;
    }
    Split({frame.output.data(), produced}, records);
    if (error == boost::beast::zlib::error::end_of_stream)
    {
        // The last record need not end with a newline.
        Split("\n", records);
        frame.ended = true;
    }
    else if (error || (taken == 0 && produced == 0))
    {
        // A stream that fails or stops short loses only its unfinished line.
        frame.ended = true;
        frame.line.clear();
    }
    return true;
}

// Parses each record line that `text` completes.
void Snapshot::Split(std::string_view text, std::vector<Record>& records)
{
    auto& frame = *frame_;
    while (!text.empty())
    {
        auto end = text.find('\n');
        auto piece = text.substr(0, end);
        if (!frame.oversized && frame.line.size() + piece.size() > maxRecord)
        {
            frame.oversized = true;
            frame.line.clear();
        }
        if (!frame.oversized)
        {
            frame.line.append(piece);
        }
        if (end == std::string_view::npos)
        {
            return;
        }
        text.remove_prefix(end + 1);
        if (!frame.oversized && !frame.line.empty())
        {
            auto json = ParseJson(frame.line);
            auto record = json ? Record::Parse(*json) : std::nullopt;
            if (record)
            {
                records.push_back(std::move(*record));
            }
        }
        frame.line.clear();
        frame.oversized = false;
    }
}

// Consumes bytes of the current frame.
void Snapshot::Skip(std::size_t count)
{
    offset_ += count;
    frame_->left -= count;
}
}
