#include "Record.h"
#include "Owned.h"
#include <Windows.h>
#include <algorithm>
#include <cstring>
#include <limits>

namespace tt
{
namespace
{
// What Windows says of an open file: its identity, size and last write.
struct Seen
{
    FILE_ID_INFO identity{};
    std::int64_t size = 0;
    std::int64_t written = 0;
    // The volume opens files by their ID, so the ID stays with the file.
    bool stable = false;
};

// Opens the file or folder for its attributes only, so a file that another
// program writes or holds open still answers.
HANDLE Open(std::filesystem::path const& path)
{
    return CreateFileW(path.c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, nullptr);
}

std::optional<Seen> See(HANDLE file)
{
    Seen seen;
    FILE_STANDARD_INFO standard{};
    FILE_BASIC_INFO basic{};
    DWORD flags = 0;
    if (!GetFileInformationByHandleEx(file, FileIdInfo, &seen.identity, sizeof(seen.identity)) ||
        !GetFileInformationByHandleEx(file, FileStandardInfo, &standard, sizeof(standard)) ||
        !GetFileInformationByHandleEx(file, FileBasicInfo, &basic, sizeof(basic)) ||
        !GetVolumeInformationByHandleW(file, nullptr, 0, nullptr, nullptr, &flags, nullptr, 0))
    {
        return std::nullopt;
    }
    seen.size = standard.EndOfFile.QuadPart;
    seen.written = basic.LastWriteTime.QuadPart;
    seen.stable = (flags & FILE_SUPPORTS_OPEN_BY_FILE_ID) != 0;
    return seen;
}

std::optional<Seen> See(std::filesystem::path const& path)
{
    auto file = Open(path);
    if (file == INVALID_HANDLE_VALUE)
        return std::nullopt;
    OwnedHandle owned(file);
    return See(file);
}

std::string Hex(std::array<std::uint8_t, 16> const& bytes)
{
    constexpr char digits[] = "0123456789abcdef";
    std::string text;
    for (auto byte : bytes)
    {
        text.push_back(digits[byte >> 4]);
        text.push_back(digits[byte & 15]);
    }
    return text;
}

std::optional<std::array<std::uint8_t, 16>> FromHex(std::string const& text)
{
    if (text.size() != 32)
        return std::nullopt;
    std::array<std::uint8_t, 16> bytes{};
    for (std::size_t index = 0; index < text.size(); ++index)
    {
        auto digit = text[index];
        int value = digit >= '0' && digit <= '9' ? digit - '0' : digit >= 'a' && digit <= 'f' ? digit - 'a' + 10 : -1;
        if (value < 0)
            return std::nullopt;
        bytes[index / 2] = static_cast<std::uint8_t>(bytes[index / 2] << 4 | value);
    }
    return bytes;
}

bool InRecycleBin(std::filesystem::path const& path)
{
    return std::any_of(path.begin(), path.end(), [](auto const& part)
    {
        return CompareStringOrdinal(part.c_str(), -1, L"$Recycle.Bin", -1, TRUE) == CSTR_EQUAL;
    });
}

// The path Windows gives an open file, without the `\\?\` prefix.
std::optional<std::filesystem::path> FinalPath(HANDLE file)
{
    std::wstring path(MAX_PATH, L'\0');
    auto length = GetFinalPathNameByHandleW(file, path.data(), static_cast<DWORD>(path.size()),
        FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
    if (length >= path.size())
    {
        path.resize(length);
        length = GetFinalPathNameByHandleW(file, path.data(), static_cast<DWORD>(path.size()),
            FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
    }
    if (length == 0 || length >= path.size())
        return std::nullopt;
    path.resize(length);
    if (path.starts_with(LR"(\\?\UNC\)"))
        return LR"(\\)" + path.substr(8);
    if (path.starts_with(LR"(\\?\)"))
        return path.substr(4);
    return path;
}
}

std::optional<Record> Record::Take(std::filesystem::path const& path)
{
    auto seen = See(path);
    if (!seen)
        return std::nullopt;
    Record record;
    record.volume = seen->identity.VolumeSerialNumber;
    record.written = seen->written;
    if (seen->stable)
    {
        record.id.emplace();
        std::memcpy(record.id->data(), seen->identity.FileId.Identifier, record.id->size());
    }
    return record;
}

bool Record::Proves(std::filesystem::path const& path, std::int64_t size) const
{
    auto seen = See(path);
    return id && seen && seen->identity.VolumeSerialNumber == volume &&
        std::memcmp(seen->identity.FileId.Identifier, id->data(), id->size()) == 0 &&
        seen->size == size && seen->written == written;
}

bool Record::Matches(std::filesystem::path const& path, std::int64_t size) const
{
    auto seen = See(path);
    return seen && seen->size == size && seen->written == written;
}

std::optional<std::filesystem::path> Record::Locate(std::filesystem::path const& folder) const
{
    if (!id)
        return std::nullopt;
    auto existing = folder;
    auto opened = Open(existing);
    while (opened == INVALID_HANDLE_VALUE && existing.has_relative_path())
    {
        existing = existing.parent_path();
        opened = Open(existing);
    }
    if (opened == INVALID_HANDLE_VALUE)
        return std::nullopt;
    OwnedHandle anchor(opened);
    FILE_ID_INFO identity{};
    if (!GetFileInformationByHandleEx(opened, FileIdInfo, &identity, sizeof(identity)) ||
        identity.VolumeSerialNumber != volume)
    {
        return std::nullopt;
    }
    FILE_ID_DESCRIPTOR descriptor{};
    descriptor.dwSize = sizeof(descriptor);
    descriptor.Type = ExtendedFileIdType;
    std::memcpy(descriptor.ExtendedFileId.Identifier, id->data(), id->size());
    auto file = OpenFileById(opened, &descriptor, FILE_READ_ATTRIBUTES,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, 0);
    if (file == INVALID_HANDLE_VALUE)
        return std::nullopt;
    OwnedHandle found(file);
    auto path = FinalPath(file);
    if (!path || InRecycleBin(*path))
        return std::nullopt;
    return path;
}

Json ToJson(SavedRecords const& saved)
{
    Json files = Json::array();
    for (auto const& [file, record] : saved.records)
    {
        Json entry = {{"index", static_cast<int>(file)}, {"volume", record.volume}, {"written", record.written}};
        if (record.id)
            entry["id"] = Hex(*record.id);
        files.push_back(std::move(entry));
    }
    return {{"files", std::move(files)}, {"pass", saved.pass}};
}

SavedRecords ReadRecords(Json const& saved)
{
    SavedRecords read{.pass = ReadSaved(saved, "pass", false)};
    auto& records = read.records;
    auto files = saved.find("files");
    if (files == saved.end() || !files->is_array())
        return read;
    for (auto const& entry : *files)
    {
        if (!entry.is_object())
            continue;
        auto index = entry.find("index");
        auto volume = entry.find("volume");
        auto written = entry.find("written");
        if (index == entry.end() || !index->is_number_integer() || index->get<std::int64_t>() < 0 ||
            index->get<std::int64_t>() > std::numeric_limits<int>::max() || volume == entry.end() ||
            !volume->is_number_unsigned() || written == entry.end() || !written->is_number_integer())
        {
            continue;
        }
        Record record{volume->get<std::uint64_t>(), std::nullopt, written->get<std::int64_t>()};
        if (auto id = entry.find("id"); id != entry.end())
        {
            if (!id->is_string())
                continue;
            record.id = FromHex(id->get<std::string>());
            if (!record.id)
                continue;
        }
        records.emplace(lt::file_index_t(index->get<int>()), record);
    }
    return read;
}
}
