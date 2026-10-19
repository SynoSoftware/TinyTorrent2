#include "Payload.h"
#include "Engine.h"
#include "Owned.h"
#include <Windows.h>
#include <winioctl.h>
#include <algorithm>
#include <chrono>
#include <cstddef>
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
    std::uint64_t size;
    std::uint64_t reported = 0;
};

COPYFILE2_MESSAGE_ACTION CALLBACK Report(COPYFILE2_MESSAGE const* message, void* context)
{
    auto& copying = *static_cast<Copying*>(context);
    if (message->Type == COPYFILE2_CALLBACK_CHUNK_FINISHED)
    {
        // The total also counts alternate data streams, which the size leaves out.
        auto transferred = std::min<std::uint64_t>(message->Info.ChunkFinished.uliTotalBytesTransferred.QuadPart,
            copying.size);
        if (transferred > copying.reported)
        {
            copying.progress.done += transferred - copying.reported;
            copying.reported = transferred;
        }
    }
    return copying.stop ? COPYFILE2_PROGRESS_CANCEL : COPYFILE2_PROGRESS_CONTINUE;
}

// The disk changes of one Move, so a failure can undo them.
class Placement
{
public:
    explicit Placement(Progress& progress) : progress_(progress) {}

    // Places every file and returns the first failure.
    std::optional<std::string> Place(std::vector<File> const& files);
    // Deletes the copies this move made and renames files back.
    MoveFailure Undo();

private:
    struct Copy
    {
        File const* file;
        std::uint64_t size;
        DWORD flags;
        bool parallel;
        // This move created the destination, so a failure deletes it.
        bool owned = false;
    };

    std::optional<std::string> CopyAll();
    void Run(Copy& copy);

    Progress& progress_;
    std::vector<File const*> renamed_;
    std::vector<Copy> copies_;
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
            if (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND)
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
        // cannot stay sparse, which can be far more than it holds.
        needed[to.root] += keepsSparse ? Allocated(file->source, size) : size;
        copies_.push_back({file, size, flags, local && !from.seeks && !to.seeks && size < large});
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
    Copying copying{progress_, stop_, copy.size};
    COPYFILE2_EXTENDED_PARAMETERS parameters{sizeof(parameters), copy.flags, nullptr, &Report, &copying};
    auto result = CopyFile2(Extended(copy.file->source).c_str(), Extended(copy.file->destination).c_str(),
        &parameters);
    auto error = SUCCEEDED(result) ? ERROR_SUCCESS :
        HRESULT_FACILITY(result) == FACILITY_WIN32 ? DWORD(HRESULT_CODE(result)) : DWORD(result);
    // A destination that already existed belongs to another program and stays.
    copy.owned = error != ERROR_FILE_EXISTS;
    if (error == ERROR_SUCCESS)
    {
        progress_.done += copy.size - copying.reported;
        return;
    }
    std::lock_guard lock(gate_);
    // A copy that another failure cancelled reports nothing of its own.
    if (!failure_)
        failure_ = Describe(error == ERROR_FILE_EXISTS ? copy.file->destination : copy.file->source, error);
    stop_ = true;
}

MoveFailure Placement::Undo()
{
    std::vector<std::filesystem::path> copies;
    for (auto const& copy : copies_)
    {
        if (copy.owned)
            copies.push_back(copy.file->destination);
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
    return std::unexpected(Failure{placement.Undo(), std::move(*failure)});
}

void Delete(std::vector<std::filesystem::path> const& files)
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
}
