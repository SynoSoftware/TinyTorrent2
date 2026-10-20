#include "Payload.h"
#include "Engine.h"
#include "Owned.h"
#include <Windows.h>
#include <winioctl.h>
#include <algorithm>
#include <chrono>
#include <cstddef>
#include <cstdlib>
#include <exception>
#include <map>
#include <mutex>
#include <optional>
#include <set>
#include <stdexcept>
#include <system_error>
#include <thread>

namespace tt::Payload
{
namespace
{
// Files from this size are copied without the system cache, as AngelCOPY's
// engine does, because caching them only evicts other programs' data.
constexpr std::uint64_t large = 32ull << 20;
// AngelCOPY measured 16 parallel copies as fastest on NVMe. More were slower,
// because NTFS creates the files of one folder one at a time.
constexpr unsigned copiers = 16;
// A worker takes at most this many files of one folder, so a large folder
// spreads over several workers.
constexpr std::size_t batch = 256;
// The first Windows build whose CopyFile2 keeps a sparse file sparse.
constexpr DWORD sparseBuild = 22621;
// Every this many bytes a copy flushes its file and records its proof, so a
// crash copies at most this much again. A flush took up to 15 ms locally and
// 50 ms over SMB for a gibibyte that copies in 2 to 9 seconds.
constexpr std::uint64_t proofInterval = 1ull << 30;
// A continued copy trusts its proof once these bytes before it match the
// source, because a crash can leave cached writes on disk out of order.
constexpr std::uint64_t proofCheck = 4ull << 20;
// The bytes that a continued copy reads, compares and writes at a time.
constexpr DWORD block = 1u << 20;

// Windows takes a path longer than MAX_PATH only with this prefix, and
// libtorrent creates such paths. The paths are full and normalized.
std::wstring Extended(std::filesystem::path const& path)
{
    auto const& text = path.native();
    if (text.starts_with(LR"(\\?\)"))
        return text;
    if (text.starts_with(LR"(\\)"))
        return LR"(\\?\UNC\)" + text.substr(2);
    return LR"(\\?\)" + text;
}

std::string Describe(std::filesystem::path const& path, std::error_code error)
{
    return Utf8(path.wstring()) + ": " + error.message();
}

std::string Describe(std::filesystem::path const& path, DWORD error)
{
    return Describe(path, std::error_code(static_cast<int>(error), std::system_category()));
}

// The running Windows build, whatever the executable's manifest declares.
DWORD Build()
{
    static DWORD const build = []
    {
        RTL_OSVERSIONINFOW version{sizeof(version)};
        auto query = reinterpret_cast<LONG(WINAPI*)(RTL_OSVERSIONINFOW*)>(
            GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "RtlGetVersion"));
        return query && query(&version) == 0 ? version.dwBuildNumber : 0;
    }();
    return build;
}

struct Volume
{
    // The root, or the folder itself when Windows names no root; free-space
    // queries accept either.
    std::wstring root;
    bool remote = false;
    // Without an answer the volume counts as a hard disk, which only keeps
    // its copies sequential.
    bool seeks = true;
    bool sparse = false;
};

// The root without the long-path prefix, which not every volume query
// accepts. A root is short enough to need none.
std::wstring Plain(std::wstring root)
{
    if (root.starts_with(LR"(\\?\UNC\)"))
        return LR"(\\)" + root.substr(8);
    if (root.starts_with(LR"(\\?\)") && root.size() > 5 && root[5] == L':')
        return root.substr(4);
    return root;
}

// The volume that holds `folder`. No query needs administrator rights.
Volume Inspect(std::filesystem::path const& folder)
{
    Volume volume;
    auto path = Extended(folder);
    std::wstring root(path.size() + 1, L'\0');
    if (!GetVolumePathNameW(path.c_str(), root.data(), static_cast<DWORD>(root.size())))
    {
        volume.root = path;
        return volume;
    }
    volume.root = Plain(root.c_str());
    volume.remote = GetDriveTypeW(volume.root.c_str()) == DRIVE_REMOTE;
    DWORD features = 0;
    if (GetVolumeInformationW(volume.root.c_str(), nullptr, 0, nullptr, nullptr, &features, nullptr, 0))
        volume.sparse = (features & FILE_SUPPORTS_SPARSE_FILES) != 0;
    wchar_t name[MAX_PATH]{};
    if (volume.remote || !GetVolumeNameForVolumeMountPointW(volume.root.c_str(), name, MAX_PATH))
        return volume;
    // A volume opens by its name without the trailing backslash.
    std::wstring device(name);
    device.pop_back();
    auto handle = CreateFileW(device.c_str(), 0, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0,
        nullptr);
    if (handle == INVALID_HANDLE_VALUE)
        return volume;
    OwnedHandle owned(handle);
    STORAGE_PROPERTY_QUERY query{StorageDeviceSeekPenaltyProperty, PropertyStandardQuery};
    DEVICE_SEEK_PENALTY_DESCRIPTOR penalty{};
    DWORD returned = 0;
    if (DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, &query, sizeof(query), &penalty, sizeof(penalty),
        &returned, nullptr) && returned > offsetof(DEVICE_SEEK_PENALTY_DESCRIPTOR, IncursSeekPenalty))
        volume.seeks = penalty.IncursSeekPenalty;
    return volume;
}

// The bytes the file occupies on disk; the holes of a sparse file take none.
std::uint64_t Allocated(std::filesystem::path const& file, std::uint64_t size)
{
    DWORD high = 0;
    auto low = GetCompressedFileSizeW(Extended(file).c_str(), &high);
    if (low == INVALID_FILE_SIZE && GetLastError() != NO_ERROR)
        return size;
    return (std::uint64_t(high) << 32) | low;
}

// The byte ranges of a sparse file that hold data, in order, or nothing when
// Windows cannot list them.
std::optional<std::vector<FILE_ALLOCATED_RANGE_BUFFER>> DataRanges(std::filesystem::path const& file,
    std::uint64_t size)
{
    auto handle = CreateFileW(Extended(file).c_str(), FILE_READ_DATA,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, 0, nullptr);
    if (handle == INVALID_HANDLE_VALUE)
        return std::nullopt;
    OwnedHandle owned(handle);
    std::vector<FILE_ALLOCATED_RANGE_BUFFER> ranges;
    FILE_ALLOCATED_RANGE_BUFFER query{};
    query.Length.QuadPart = static_cast<LONGLONG>(size);
    FILE_ALLOCATED_RANGE_BUFFER found[64];
    while (true)
    {
        DWORD returned = 0;
        bool complete = DeviceIoControl(handle, FSCTL_QUERY_ALLOCATED_RANGES, &query, sizeof(query), found,
            sizeof(found), &returned, nullptr);
        if (!complete && GetLastError() != ERROR_MORE_DATA)
            return std::nullopt;
        auto count = returned / sizeof(found[0]);
        ranges.insert(ranges.end(), found, found + count);
        if (complete || count == 0)
            return ranges;
        auto end = ranges.back().FileOffset.QuadPart + ranges.back().Length.QuadPart;
        query.FileOffset.QuadPart = end;
        query.Length.QuadPart = static_cast<LONGLONG>(size) - end;
    }
}

// The data bytes of `ranges` between `from` and `to`. `next` is the first
// range that can end after `from`, so a copy passes each range once.
std::uint64_t DataBetween(std::vector<FILE_ALLOCATED_RANGE_BUFFER> const& ranges, std::size_t& next,
    std::uint64_t from, std::uint64_t to)
{
    std::uint64_t bytes = 0;
    for (; next < ranges.size(); ++next)
    {
        auto start = static_cast<std::uint64_t>(ranges[next].FileOffset.QuadPart);
        auto end = start + static_cast<std::uint64_t>(ranges[next].Length.QuadPart);
        if (start >= to)
            break;
        if (end > from)
            bytes += std::min(end, to) - std::max(start, from);
        if (end > to)
            break;
    }
    return bytes;
}

// The paths' indices in groups from one folder, at most `batch` each.
std::vector<std::vector<std::size_t>> Batches(std::vector<std::filesystem::path> const& paths)
{
    std::map<std::filesystem::path, std::vector<std::size_t>> folders;
    for (std::size_t index = 0; index < paths.size(); ++index)
        folders[paths[index].parent_path()].push_back(index);
    std::vector<std::vector<std::size_t>> batches;
    for (auto const& [folder, indices] : folders)
    {
        for (std::size_t first = 0; first < indices.size(); first += batch)
            batches.emplace_back(indices.begin() + first, indices.begin() + std::min(first + batch, indices.size()));
    }
    return batches;
}

// Runs `work` for each index below `count` on up to `workers` threads, the
// calling thread among them. After every thread ends, it rethrows the first
// exception, which also stops the other threads taking indices.
template <typename Work>
void Parallel(std::size_t count, unsigned workers, Work const& work)
{
    std::atomic<std::size_t> next{0};
    std::mutex gate;
    std::exception_ptr failure;
    auto run = [&]
    {
        try
        {
            for (auto index = next++; index < count; index = next++)
                work(index);
        }
        catch (...)
        {
            std::lock_guard lock(gate);
            if (!failure)
                failure = std::current_exception();
            next = count;
        }
    };
    {
        std::vector<std::jthread> threads;
        for (std::size_t thread = 1; thread < std::min<std::size_t>(workers, count); ++thread)
            threads.emplace_back(run);
        run();
    }
    if (failure)
        std::rethrow_exception(failure);
}

// Deletes the file. Antivirus scanners, indexers and players hold a file open
// for a moment, so a refused delete is tried again for up to 200 ms, each
// wait 1.2 times longer, as rimraf does.
std::error_code Remove(std::filesystem::path const& file)
{
    std::filesystem::path path = Extended(file);
    bool writable = false;
    std::chrono::milliseconds wait{1};
    std::chrono::milliseconds waited{0};
    for (;;)
    {
        std::error_code error;
        std::filesystem::remove(path, error);
        if (!error)
            return {};
        auto code = static_cast<DWORD>(error.value());
        // The classic delete, which FAT volumes use, refuses a read-only file.
        if (code == ERROR_ACCESS_DENIED && !writable)
        {
            writable = true;
            auto attributes = GetFileAttributesW(path.c_str());
            if (attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_READONLY) &&
                SetFileAttributesW(path.c_str(), attributes & ~FILE_ATTRIBUTE_READONLY))
                continue;
        }
        if (code != ERROR_SHARING_VIOLATION && code != ERROR_LOCK_VIOLATION && code != ERROR_ACCESS_DENIED)
            return error;
        wait = std::chrono::milliseconds((wait.count() * 6 + 4) / 5);
        waited += wait;
        if (waited > std::chrono::milliseconds(200))
            return error;
        std::this_thread::sleep_for(wait);
    }
}

struct Copying
{
    Progress& progress;
    std::atomic<bool> const& stop;
    std::filesystem::path const& destination;
    std::uint64_t size;
    // The data ranges of a copy that keeps its holes, which it skips at once,
    // so only these bytes count as copied.
    std::vector<FILE_ALLOCATED_RANGE_BUFFER> const* data = nullptr;
    // The copy proves its bytes and parks when Exit cancels it. A parallel
    // copy is small, so it is cancelled and copied again instead.
    bool proves = false;
    std::size_t next = 0;
    std::uint64_t reported = 0;
    std::uint64_t proven = 0;
};

COPYFILE2_MESSAGE_ACTION CALLBACK Report(COPYFILE2_MESSAGE const* message, void* context)
{
    auto& copying = *static_cast<Copying*>(context);
    if (message->Type == COPYFILE2_CALLBACK_CHUNK_FINISHED)
    {
        auto const& chunk = message->Info.ChunkFinished;
        // The total also counts alternate data streams, which the size leaves out.
        auto transferred = std::min<std::uint64_t>(chunk.uliTotalBytesTransferred.QuadPart, copying.size);
        if (transferred > copying.reported)
        {
            auto bytes = copying.data ? DataBetween(*copying.data, copying.next, copying.reported, transferred)
                                      : transferred - copying.reported;
            copying.progress.done += bytes;
            copying.progress.copied += bytes;
            copying.reported = transferred;
        }
        // The data stream goes first, so a later stream finds it complete.
        auto written = chunk.dwStreamNumber == 1 ?
            std::min<std::uint64_t>(chunk.uliStreamBytesTransferred.QuadPart, copying.size) : copying.size;
        bool parks = copying.proves && copying.progress.cancel == MoveCancel::Keep;
        // A flush during a later stream would prove nothing of the data
        // stream, which it may not reach.
        if (chunk.dwStreamNumber == 1 && copying.proves && (written >= copying.proven + proofInterval || parks))
        {
            // The flush writes every byte CopyFile2 has written to the file
            // so far, through CopyFile2's own handle.
            if (FlushFileBuffers(chunk.hDestinationFile))
            {
                copying.proven = written;
                copying.progress.Prove({copying.destination, written});
            }
        }
        if (parks)
        {
            copying.progress.parked = true;
            Sleep(INFINITE);
        }
    }
    // CopyFile2 deletes the file it cancels, so a cancel that keeps the copy
    // never cancels it: the copy parks at its next chunk, or a parallel copy
    // finishes.
    auto cancel = copying.progress.cancel.load();
    bool cancels = cancel == MoveCancel::Undo || (cancel == MoveCancel::None && copying.stop);
    return cancels ? COPYFILE2_PROGRESS_CANCEL : COPYFILE2_PROGRESS_CONTINUE;
}

// Writes the file's cached bytes to the disk. Returns the Windows error, or
// ERROR_SUCCESS.
DWORD Flush(std::wstring const& path)
{
    auto file = CreateFileW(path.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
    if (file == INVALID_HANDLE_VALUE)
        return GetLastError();
    OwnedHandle owned(file);
    return FlushFileBuffers(file) ? ERROR_SUCCESS : GetLastError();
}

bool ReadAt(HANDLE file, std::uint64_t offset, std::byte* buffer, DWORD count)
{
    OVERLAPPED at{};
    at.Offset = static_cast<DWORD>(offset);
    at.OffsetHigh = static_cast<DWORD>(offset >> 32);
    DWORD read = 0;
    return ReadFile(file, buffer, count, &read, &at) && read == count;
}

bool WriteAt(HANDLE file, std::uint64_t offset, std::byte const* buffer, DWORD count)
{
    OVERLAPPED at{};
    at.Offset = static_cast<DWORD>(offset);
    at.OffsetHigh = static_cast<DWORD>(offset >> 32);
    DWORD written = 0;
    return WriteFile(file, buffer, count, &written, &at) && written == count;
}

// Copies the source's alternate data streams, such as its Mark of the Web, as
// CopyFile2 does. They are small, and a stream that fails to copy is left out.
void CopyStreams(std::filesystem::path const& source, std::wstring const& target)
{
    WIN32_FIND_STREAM_DATA stream{};
    auto find = FindFirstStreamW(Extended(source).c_str(), FindStreamInfoStandard, &stream, 0);
    if (find == INVALID_HANDLE_VALUE)
        return;
    do
    {
        std::wstring name = stream.cStreamName;
        if (name == L"::$DATA" || stream.StreamSize.QuadPart > 16ll << 20)
            continue;
        // ":name:$DATA" opens as ":name".
        auto suffix = name.substr(0, name.rfind(L':'));
        auto from = CreateFileW((Extended(source) + suffix).c_str(), GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, 0, nullptr);
        if (from == INVALID_HANDLE_VALUE)
            continue;
        OwnedHandle reading(from);
        auto to = CreateFileW((target + suffix).c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, 0, nullptr);
        if (to == INVALID_HANDLE_VALUE)
            continue;
        OwnedHandle writing(to);
        std::vector<std::byte> bytes(static_cast<std::size_t>(stream.StreamSize.QuadPart));
        if (!bytes.empty() && ReadAt(from, 0, bytes.data(), static_cast<DWORD>(bytes.size())))
            WriteAt(to, 0, bytes.data(), static_cast<DWORD>(bytes.size()));
    } while (FindNextStreamW(find, &stream));
    FindClose(find);
}

// The disk changes of one Move, so a failure can undo them.
class Placement
{
public:
    explicit Placement(Progress& progress) : progress_(progress) {}

    // Places every file and returns the first failure.
    std::optional<std::string> Place(std::vector<File> const& files);
    // Deletes the copies and folders this move made and renames files back.
    MoveFailure Undo();

private:
    struct Copy
    {
        File const* file;
        std::uint64_t size;
        DWORD flags;
        bool parallel;
        // The source's data ranges when the copy keeps its holes.
        std::optional<std::vector<FILE_ALLOCATED_RANGE_BUFFER>> data;
        // This move created the copy, so a failure deletes it.
        bool owned = false;
        // The copy has its final name.
        bool complete = false;
    };

    std::optional<std::string> CopyAll();
    void Run(Copy& copy);
    // Finishes the unfinished copy at `partial`, which an earlier move left,
    // and returns the Windows error, or ERROR_SUCCESS.
    DWORD Continue(Copy const& copy, std::wstring const& partial);

    Progress& progress_;
    std::vector<File const*> renamed_;
    std::vector<Copy> copies_;
    std::set<std::filesystem::path> created_;
    std::atomic<bool> stop_{false};
    std::mutex gate_;
    std::optional<std::string> failure_;
};

std::optional<std::string> Placement::Place(std::vector<File> const& files)
{
    struct Found
    {
        File const* file;
        std::uint64_t size;
        bool sparse;
    };
    std::vector<Found> found;
    std::set<std::filesystem::path> folders;
    for (auto const& file : files)
    {
        WIN32_FILE_ATTRIBUTE_DATA data;
        if (!GetFileAttributesExW(Extended(file.source).c_str(), GetFileExInfoStandard, &data))
        {
            auto error = GetLastError();
            // Without its source, an unfinished copy cannot be checked, and
            // it may hold the only copy of the bytes, so the move stops.
            bool unfinished = GetFileAttributesW(Extended(Partial(file.destination)).c_str()) != INVALID_FILE_ATTRIBUTES;
            if ((error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND) && !unfinished)
                continue;
            return Describe(file.source, error);
        }
        auto size = (std::uint64_t(data.nFileSizeHigh) << 32) | data.nFileSizeLow;
        found.push_back({&file, size, bool(data.dwFileAttributes & FILE_ATTRIBUTE_SPARSE_FILE)});
        folders.insert(file.destination.parent_path());
        progress_.total += size;
    }
    // Workers would otherwise create the same folder at the same time.
    for (auto const& folder : folders)
    {
        std::error_code error;
        for (auto missing = folder; missing.has_relative_path() &&
            !std::filesystem::exists(Extended(missing), error) && !error; missing = missing.parent_path())
            created_.insert(missing);
        std::filesystem::create_directories(Extended(folder), error);
        if (error)
            return Describe(folder, error);
    }
    std::map<std::filesystem::path, Volume> volumes;
    auto volume = [&volumes](std::filesystem::path const& folder) -> Volume const&
    {
        auto known = volumes.find(folder);
        return known != volumes.end() ? known->second : volumes.emplace(folder, Inspect(folder)).first->second;
    };
    std::map<std::wstring, std::uint64_t> needed;
    for (auto const& [file, size, sparse] : found)
    {
        if (MoveFileExW(Extended(file->source).c_str(), Extended(file->destination).c_str(), 0))
        {
            renamed_.push_back(file);
            progress_.done += size;
            continue;
        }
        if (auto error = GetLastError(); error != ERROR_NOT_SAME_DEVICE)
            return Describe(file->source, error);
        auto const& from = volume(file->source.parent_path());
        auto const& to = volume(file->destination.parent_path());
        bool local = !from.remote && !to.remote;
        bool keepsSparse = sparse && to.sparse && Build() >= sparseBuild;
        DWORD flags = COPY_FILE_FAIL_IF_EXISTS | COPY_FILE_COPY_SYMLINK;
        if (local && size >= large)
            flags |= COPY_FILE_NO_BUFFERING;
        if (keepsSparse)
            flags |= COPY_FILE_ENABLE_SPARSE_COPY;
        // An unfinished torrent's sparse file takes its full size where it
        // cannot stay sparse, which can be far more than it holds. An
        // unfinished copy already holds part of it.
        auto partial = Extended(Partial(file->destination));
        auto space = keepsSparse ? Allocated(file->source, size) : size;
        if (GetFileAttributesW(partial.c_str()) != INVALID_FILE_ATTRIBUTES)
            space -= std::min(space, Allocated(partial, 0));
        needed[to.root] += space;
        std::optional<std::vector<FILE_ALLOCATED_RANGE_BUFFER>> data;
        if (keepsSparse)
            data = DataRanges(file->source, size);
        if (data)
        {
            std::uint64_t bytes = 0;
            for (auto const& range : *data)
                bytes += static_cast<std::uint64_t>(range.Length.QuadPart);
            progress_.total -= size - std::min(bytes, size);
        }
        copies_.push_back({file, size, flags, local && !from.seeks && !to.seeks && size < large, std::move(data)});
    }
    for (auto const& [root, bytes] : needed)
    {
        ULARGE_INTEGER available{};
        if (!GetDiskFreeSpaceExW(root.c_str(), &available, nullptr, nullptr))
            return Describe(root, GetLastError());
        if (available.QuadPart < bytes)
            return Describe(root, static_cast<DWORD>(ERROR_DISK_FULL));
    }
    return CopyAll();
}

// Small files between solid-state volumes copy in parallel. Every other copy
// runs alone: parallel copies make a hard disk seek, and a large file already
// keeps the disks busy.
std::optional<std::string> Placement::CopyAll()
{
    std::vector<std::size_t> parallel;
    std::vector<std::filesystem::path> destinations;
    for (std::size_t index = 0; index < copies_.size(); ++index)
    {
        if (copies_[index].parallel)
        {
            parallel.push_back(index);
            destinations.push_back(copies_[index].file->destination);
        }
    }
    auto batches = Batches(destinations);
    Parallel(batches.size(), copiers, [this, &batches, &parallel](std::size_t index)
    {
        for (auto item : batches[index])
            Run(copies_[parallel[item]]);
    });
    for (auto& copy : copies_)
    {
        if (!copy.parallel)
            Run(copy);
    }
    return failure_;
}

void Placement::Run(Copy& copy)
{
    if (stop_)
        return;
    auto partial = Extended(Partial(copy.file->destination));
    DWORD error = ERROR_SUCCESS;
    // A copy that a cancel finds not started stays unstarted.
    if (progress_.cancel != MoveCancel::None)
        error = ERROR_REQUEST_ABORTED;
    // A file with the copy's name is one that an earlier move left
    // unfinished.
    else if (GetFileAttributesW(partial.c_str()) != INVALID_FILE_ATTRIBUTES)
    {
        copy.owned = true;
        error = Continue(copy, partial);
    }
    else
    {
        Copying copying{progress_, stop_, copy.file->destination, copy.size, copy.data ? &*copy.data : nullptr,
            !copy.parallel};
        COPYFILE2_EXTENDED_PARAMETERS parameters{sizeof(parameters), copy.flags, nullptr, &Report, &copying};
        auto result = CopyFile2(Extended(copy.file->source).c_str(), partial.c_str(), &parameters);
        error = SUCCEEDED(result) ? ERROR_SUCCESS :
            HRESULT_FACILITY(result) == FACILITY_WIN32 ? DWORD(HRESULT_CODE(result)) : DWORD(result);
        // A file that took the name meanwhile belongs to another program and
        // stays. CopyFile2 reports it with either code, and with access
        // denied when that file is hidden or read-only.
        copy.owned = error != ERROR_FILE_EXISTS && error != ERROR_ALREADY_EXISTS && error != ERROR_ACCESS_DENIED;
        if (error == ERROR_SUCCESS)
        {
            progress_.done += copy.size - copying.reported;
            progress_.copied += copy.size - copying.reported;
            error = Flush(partial);
        }
    }
    auto failed = copy.file->source;
    if (error == ERROR_SUCCESS)
    {
        // A rename without a flag refuses a file that took the name meanwhile.
        if (MoveFileExW(partial.c_str(), Extended(copy.file->destination).c_str(), MOVEFILE_WRITE_THROUGH))
        {
            copy.complete = true;
            return;
        }
        error = GetLastError();
        failed = copy.file->destination;
    }
    std::lock_guard lock(gate_);
    // A copy that another failure cancelled reports nothing of its own.
    if (!failure_)
        failure_ = Describe(error == ERROR_FILE_EXISTS || error == ERROR_ALREADY_EXISTS ? copy.file->destination :
            failed, error);
    stop_ = true;
}

DWORD Placement::Continue(Copy const& copy, std::wstring const& partial)
{
    auto const& file = *copy.file;
    auto source = CreateFileW(Extended(file.source).c_str(), GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN,
        nullptr);
    if (source == INVALID_HANDLE_VALUE)
        return GetLastError();
    OwnedHandle reading(source);
    auto target = CreateFileW(partial.c_str(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
        0, nullptr);
    if (target == INVALID_HANDLE_VALUE)
        return GetLastError();
    OwnedHandle writing(target);
    LARGE_INTEGER length{};
    if (!GetFileSizeEx(target, &length))
        return GetLastError();
    auto size = copy.size;
    auto present = std::min<std::uint64_t>(static_cast<std::uint64_t>(length.QuadPart), size);
    std::vector<std::byte> left(block);
    std::vector<std::byte> right(block);
    // Whether the copy holds the source's bytes from `offset` to `end`. A read
    // that fails counts as different, so the bytes are copied again.
    auto same = [&](std::uint64_t offset, std::uint64_t end)
    {
        auto count = static_cast<DWORD>(end - offset);
        bool equal = ReadAt(source, offset, left.data(), count) && ReadAt(target, offset, right.data(), count) &&
            std::equal(left.begin(), left.begin() + count, right.begin());
        progress_.compared += count;
        return equal;
    };
    // The bytes of the move's total between two offsets, which leaves out
    // the holes of a copy that keeps them. Offsets increase from call to call.
    std::size_t counted = 0;
    auto bytes = [&](std::uint64_t from, std::uint64_t to)
    {
        return copy.data ? DataBetween(*copy.data, counted, from, to) : to - from;
    };
    // The ranges of the copy to write from the source, in order.
    std::vector<std::pair<std::uint64_t, std::uint64_t>> missing;
    bool proven = file.proven > 0 && file.proven <= present;
    for (auto offset = file.proven - std::min(file.proven, proofCheck); proven && offset < file.proven; offset += block)
        proven = same(offset, std::min<std::uint64_t>(offset + block, file.proven));
    if (proven)
    {
        progress_.done += bytes(0, file.proven);
        missing.push_back({file.proven, size});
    }
    else
    {
        for (std::uint64_t offset = 0; offset < present; offset += block)
        {
            if (progress_.cancel != MoveCancel::None)
                return ERROR_REQUEST_ABORTED;
            auto end = std::min<std::uint64_t>(offset + block, present);
            if (same(offset, end))
                progress_.done += bytes(offset, end);
            else if (!missing.empty() && missing.back().second == offset)
                missing.back().second = end;
            else
                missing.push_back({offset, end});
        }
        if (present < size)
            missing.push_back({present, size});
    }
    // A copy that keeps its holes leaves the source's holes as holes.
    if (copy.data)
    {
        DWORD returned = 0;
        DeviceIoControl(target, FSCTL_SET_SPARSE, nullptr, 0, nullptr, 0, &returned, nullptr);
    }
    std::size_t next = 0;
    std::uint64_t unproven = 0;
    for (auto const& [first, last] : missing)
    {
        for (auto offset = first; offset < last;)
        {
            // Every byte before `offset` holds the source's bytes.
            if (progress_.cancel != MoveCancel::None)
            {
                if (FlushFileBuffers(target))
                    progress_.Prove({file.destination, offset});
                return ERROR_REQUEST_ABORTED;
            }
            auto end = std::min<std::uint64_t>(offset + block, last);
            auto count = static_cast<DWORD>(end - offset);
            auto data = copy.data ? DataBetween(*copy.data, next, offset, end) : count;
            if (data == 0)
            {
                FILE_ZERO_DATA_INFORMATION hole{};
                hole.FileOffset.QuadPart = static_cast<LONGLONG>(offset);
                hole.BeyondFinalZero.QuadPart = static_cast<LONGLONG>(end);
                DWORD returned = 0;
                if (!DeviceIoControl(target, FSCTL_SET_ZERO_DATA, &hole, sizeof(hole), nullptr, 0, &returned, nullptr))
                    return GetLastError();
            }
            else
            {
                if (!ReadAt(source, offset, left.data(), count) || !WriteAt(target, offset, left.data(), count))
                    return GetLastError();
                progress_.copied += data;
            }
            progress_.done += data;
            offset = end;
            unproven += count;
            if (unproven >= proofInterval && FlushFileBuffers(target))
            {
                progress_.Prove({file.destination, offset});
                unproven = 0;
            }
        }
    }
    // The size, the streams, the last write time and the attributes as
    // CopyFile2 leaves them. A stream write changes the last write time, so
    // the time comes after the streams.
    FILE_END_OF_FILE_INFO end{};
    end.EndOfFile.QuadPart = static_cast<LONGLONG>(size);
    if (!SetFileInformationByHandle(target, FileEndOfFileInfo, &end, sizeof(end)))
        return GetLastError();
    CopyStreams(file.source, partial);
    FILE_BASIC_INFO from{};
    if (!GetFileInformationByHandleEx(source, FileBasicInfo, &from, sizeof(from)))
        return GetLastError();
    FILE_BASIC_INFO to{};
    to.LastWriteTime = from.LastWriteTime;
    constexpr DWORD kept = FILE_ATTRIBUTE_READONLY | FILE_ATTRIBUTE_HIDDEN | FILE_ATTRIBUTE_SYSTEM |
        FILE_ATTRIBUTE_ARCHIVE | FILE_ATTRIBUTE_NOT_CONTENT_INDEXED;
    to.FileAttributes = from.FileAttributes & kept ? from.FileAttributes & kept : FILE_ATTRIBUTE_NORMAL;
    if (!SetFileInformationByHandle(target, FileBasicInfo, &to, sizeof(to)) || !FlushFileBuffers(target))
        return GetLastError();
    return ERROR_SUCCESS;
}

MoveFailure Placement::Undo()
{
    std::vector<std::filesystem::path> copies;
    for (auto const& copy : copies_)
    {
        if (copy.owned)
            copies.push_back(copy.complete ? copy.file->destination : Partial(copy.file->destination));
    }
    try
    {
        Delete(copies);
    }
    catch (std::exception const&)
    {
        // A copy left behind holds nothing that its source lacks.
    }
    auto failure = MoveFailure::Restored;
    for (auto const* file : renamed_)
    {
        if (!MoveFileExW(Extended(file->destination).c_str(), Extended(file->source).c_str(), 0))
            failure = MoveFailure::Split;
    }
    // A subfolder sorts after its parent, so it goes first. A folder that
    // still holds a file stays.
    for (auto folder = created_.rbegin(); folder != created_.rend(); ++folder)
    {
        std::error_code ignored;
        std::filesystem::remove(Extended(*folder), ignored);
    }
    return failure;
}
}

std::expected<void, Failure> Move(std::vector<File> const& files, Progress& progress)
{
    Placement placement(progress);
    std::optional<std::string> failure;
    try
    {
        failure = placement.Place(files);
    }
    catch (std::exception const& error)
    {
        failure = error.what();
    }
    if (!failure)
        return {};
    if (progress.cancel == MoveCancel::Keep)
        return std::unexpected(Failure{MoveFailure::Split, std::move(*failure)});
    return std::unexpected(Failure{placement.Undo(), std::move(*failure)});
}

void Delete(std::vector<std::filesystem::path> const& files, std::stop_token stop)
{
    auto batches = Batches(files);
    std::mutex gate;
    std::optional<std::string> failure;
    // One worker per logical processor, as rmbrr and FastDelete use; their
    // measurements show the gain growing with the number of cores.
    Parallel(batches.size(), std::max(1u, std::thread::hardware_concurrency()), [&](std::size_t index)
    {
        for (auto item : batches[index])
        {
            if (stop.stop_requested())
                return;
            if (auto error = Remove(files[item]))
            {
                std::lock_guard lock(gate);
                if (!failure)
                    failure = Describe(files[item], error);
            }
        }
    });
    if (failure)
        throw std::runtime_error(*failure);
}

std::filesystem::path Partial(std::filesystem::path const& destination)
{
    return destination.native() + L".!ttcopy";
}

bool IsCopy(File const& file)
{
    WIN32_FILE_ATTRIBUTE_DATA source;
    WIN32_FILE_ATTRIBUTE_DATA destination;
    if (!GetFileAttributesExW(Extended(file.source).c_str(), GetFileExInfoStandard, &source) ||
        !GetFileAttributesExW(Extended(file.destination).c_str(), GetFileExInfoStandard, &destination))
        return false;
    // CopyFile2 keeps the last write time, and the source of a moving
    // torrent stays unchanged because the torrent stays paused. FAT keeps
    // the time to 2 seconds, in local time that Windows converts with the
    // current offset, so a copy there also differs by an hour after a
    // daylight saving change.
    auto time = [](FILETIME const& value) { return (std::int64_t(value.dwHighDateTime) << 32) | value.dwLowDateTime; };
    constexpr std::int64_t second = 10'000'000;
    auto difference = std::abs(time(source.ftLastWriteTime) - time(destination.ftLastWriteTime));
    return !(destination.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) &&
        source.nFileSizeHigh == destination.nFileSizeHigh && source.nFileSizeLow == destination.nFileSizeLow &&
        (difference <= 2 * second || std::abs(difference - 3600 * second) <= 2 * second);
}
}
