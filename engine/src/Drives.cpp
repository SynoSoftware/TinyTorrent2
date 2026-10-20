#include "Drives.h"
#include "Engine.h"
#include "Owned.h"
#include <Windows.h>
#include <winioctl.h>
#include <virtdisk.h>
#include <cstddef>
#include <cwctype>
#include <vector>

#pragma comment(lib, "virtdisk.lib")

namespace tt
{
namespace
{
// Bounds the search through virtual disks that lie in virtual disks.
constexpr int hostDepth = 4;

std::wstring FinalPath(HANDLE handle, DWORD flags)
{
    std::wstring path(MAX_PATH, L'\0');
    for (;;)
    {
        // Too small a buffer returns the size it needs, with the terminator.
        auto length = GetFinalPathNameByHandleW(handle, path.data(), static_cast<DWORD>(path.size()), flags);
        if (length == 0)
            return {};
        bool fits = length < path.size();
        path.resize(length);
        if (fits)
            return path;
    }
}

// Adds the disks that `volume` lies on, and for a virtual disk the disks of
// the volume that holds its file, because its work reaches that disk too.
// `volume` is a volume name without its trailing backslash, which opens the
// volume itself. Returns whether it found the volume's disks.
bool AddDisks(std::wstring const& volume, std::set<std::string>& names, int depth)
{
    // No access right is needed for these queries, so no administrator either.
    auto handle = CreateFileW(volume.c_str(), 0, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0,
        nullptr);
    if (handle == INVALID_HANDLE_VALUE)
        return false;
    OwnedHandle owned(handle);
    std::vector<std::byte> buffer(sizeof(VOLUME_DISK_EXTENTS));
    DWORD returned = 0;
    while (!DeviceIoControl(handle, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, nullptr, 0, buffer.data(),
        static_cast<DWORD>(buffer.size()), &returned, nullptr))
    {
        auto count = reinterpret_cast<VOLUME_DISK_EXTENTS const*>(buffer.data())->NumberOfDiskExtents;
        auto size = offsetof(VOLUME_DISK_EXTENTS, Extents) + count * sizeof(DISK_EXTENT);
        if (GetLastError() != ERROR_MORE_DATA || size <= buffer.size())
            return false;
        buffer.resize(size);
    }
    auto const& extents = *reinterpret_cast<VOLUME_DISK_EXTENTS const*>(buffer.data());
    for (DWORD index = 0; index < extents.NumberOfDiskExtents; ++index)
        names.insert("disk " + std::to_string(extents.Extents[index].DiskNumber));
    if (depth == hostDepth)
        return true;
    buffer.assign(sizeof(STORAGE_DEPENDENCY_INFO), std::byte{});
    for (;;)
    {
        auto& info = *reinterpret_cast<STORAGE_DEPENDENCY_INFO*>(buffer.data());
        info.Version = STORAGE_DEPENDENCY_INFO_VERSION_2;
        ULONG used = 0;
        auto error = GetStorageDependencyInformation(handle, GET_STORAGE_DEPENDENCY_FLAG_HOST_VOLUMES,
            static_cast<ULONG>(buffer.size()), &info, &used);
        if (error == ERROR_INSUFFICIENT_BUFFER && used > buffer.size())
        {
            buffer.assign(used, std::byte{});
            continue;
        }
        // A volume on a physical disk depends on no other volume.
        if (error != ERROR_SUCCESS)
            return true;
        for (ULONG entry = 0; entry < info.NumberEntries; ++entry)
        {
            auto const* host = info.Version2Entries[entry].HostVolumeName;
            if (!host || !*host)
                continue;
            std::wstring name(host);
            if (name.ends_with(L'\\'))
                name.pop_back();
            AddDisks(name, names, depth + 1);
        }
        return true;
    }
}
}

Drives Drives::Of(std::filesystem::path const& folder)
{
    Drives drives;
    std::error_code ignored;
    auto full = std::filesystem::absolute(folder, ignored).lexically_normal();
    auto unknown = [&full]
    {
        auto name = full.wstring();
        for (auto& letter : name)
            letter = static_cast<wchar_t>(std::towlower(letter));
        return Drives{{"folder " + Utf8(name)}};
    };
    // A folder that does not exist yet, such as a move's destination, is
    // created on the drive of its nearest existing parent.
    auto handle = INVALID_HANDLE_VALUE;
    for (auto path = full; handle == INVALID_HANDLE_VALUE; path = path.parent_path())
    {
        // Backup semantics opens a folder, and opening follows links.
        handle = CreateFileW(path.c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, nullptr);
        if (handle == INVALID_HANDLE_VALUE && path == path.parent_path())
            return unknown();
    }
    OwnedHandle owned(handle);
    // A network folder, also through a mapped drive letter, is \\?\UNC\server\...
    // The engine cannot see the server's disks, so the server is the drive.
    constexpr std::wstring_view unc = LR"(\\?\UNC\)";
    if (auto path = FinalPath(handle, VOLUME_NAME_DOS); path.starts_with(unc))
    {
        auto server = path.substr(unc.size(), path.find(L'\\', unc.size()) - unc.size());
        for (auto& letter : server)
            letter = static_cast<wchar_t>(std::towlower(letter));
        drives.names.insert("server " + Utf8(server));
        return drives;
    }
    // A local folder is \\?\Volume{...}\..., which names its volume.
    auto path = FinalPath(handle, VOLUME_NAME_GUID);
    auto end = path.find(L'\\', 4);
    if (end == std::wstring::npos || !AddDisks(path.substr(0, end), drives.names, 0))
        return unknown();
    return drives;
}
}
