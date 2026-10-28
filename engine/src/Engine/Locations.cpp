#include "Engine/State.h"
#include "Owned.h"
#include <Windows.h>
#include <winioctl.h>
#include <libtorrent/aux_/path.hpp>
#include <cstring>
#include <cwchar>
#include <iterator>
#include <set>
#include <variant>
#include <vector>

// Work in a folder belongs to the drive or server the folder really is on, not
// to the letter in its path: C:\Media that links to \\nas\media belongs to the
// server nas. Each folder the engine uses is looked up on workers and kept in
// `locations`, and RootOf and NoticeRoot read it, so a share that hangs holds
// up only its own work and its location notice names it.
namespace tt
{
namespace
{
// Windows follows at most 63 reparse points in one path, so a loop of links
// ends there too.
constexpr int linkLimit = 63;
// A folder in use is looked up again this often, so a link that now points
// elsewhere moves its work.
constexpr auto lookupInterval = std::chrono::minutes(1);
// REPARSE_DATA_BUFFER, which only the driver kit's ntifs.h declares: the tag,
// the data length and a reserved word, then the substitute name's offset and
// length in bytes from the names. A symbolic link has a flags word before its
// names; a mount point, which is also a junction, has none.
constexpr std::size_t reparseNames = 8;
constexpr std::size_t linkNames = 20;
constexpr std::size_t mountNames = 16;
constexpr ULONG relativeLink = 1;

// A link that the walk cannot follow, such as one it cannot read, or a volume
// mounted in a folder without a drive letter. The walk ends at it, because
// reading past it would open its target.
struct Opaque
{
};
// What the walk finds at one part of a path: an ordinary entry, a link it
// cannot follow, or the target of a link.
using Entry = std::variant<std::monostate, Opaque, std::filesystem::path>;

// The Win32 path of a path that a link or a drive letter names: \??\C:\a is
// C:\a, \??\UNC\nas\a is \\nas\a, and a volume is its drive letter. Nothing for
// another form, such as a volume without a letter.
std::optional<std::filesystem::path> Win32Path(std::wstring path)
{
    for (std::wstring_view prefix : {LR"(\??\)", LR"(\\?\)"})
    {
        if (path.starts_with(prefix))
        {
            path.erase(0, prefix.size());
            break;
        }
    }
    if (path.starts_with(LR"(UNC\)"))
        return std::filesystem::path(LR"(\\)" + path.substr(4));
    if (path.size() >= 2 && path[1] == L':')
        return std::filesystem::path(path);
    if (!path.starts_with(L"Volume{"))
        return std::nullopt;
    auto end = path.find(L'\\');
    auto volume = LR"(\\?\)" + path.substr(0, end) + L'\\';
    auto rest = end == std::wstring::npos ? std::wstring() : path.substr(end + 1);
    std::wstring names(MAX_PATH, L'\0');
    DWORD needed = 0;
    while (!GetVolumePathNamesForVolumeNameW(volume.c_str(), names.data(), static_cast<DWORD>(names.size()), &needed))
    {
        if (GetLastError() != ERROR_MORE_DATA || needed <= names.size())
            return std::nullopt;
        names.resize(needed);
    }
    // The names are a list of strings that ends with an empty one.
    for (auto name = names.c_str(); *name; name += std::wcslen(name) + 1)
    {
        std::wstring_view mount(name);
        if (mount.size() == 3 && mount[1] == L':')
            return std::filesystem::path(mount) / rest;
    }
    return std::nullopt;
}

// The folder or share that a subst or mapped drive letter, such as T:, stands
// for. Nothing for the letter of a local volume, or a letter not defined.
// QueryDosDevice reads the letter's definition, so it touches no drive.
std::optional<std::filesystem::path> DriveTarget(std::wstring const& letter)
{
    std::wstring device(MAX_PATH, L'\0');
    if (!QueryDosDeviceW(letter.c_str(), device.data(), static_cast<DWORD>(device.size())))
        return std::nullopt;
    device.resize(std::wcslen(device.c_str()));
    if (device.starts_with(LR"(\??\)"))
        return Win32Path(device);
    // A mapped letter names its network redirector, such as
    // \Device\Mup\;LanmanRedirector\;Z:0000000000012345\nas\media: what follows
    // the last part that starts with ; is the server and share.
    std::wstring share;
    if (auto last = device.rfind(LR"(\;)"); last != std::wstring::npos)
    {
        auto next = device.find(L'\\', last + 2);
        if (next == std::wstring::npos)
            return std::nullopt;
        share = device.substr(next + 1);
    }
    else if (constexpr std::wstring_view mup = LR"(\Device\Mup\)"; device.starts_with(mup))
        share = device.substr(mup.size());
    else
        return std::nullopt;
    return std::filesystem::path(LR"(\\)" + share);
}

// Reads `path` itself, never what it links to: its attributes, and for a
// symbolic link, junction or mount point the target from its reparse data,
// which the volume that holds the link keeps.
Entry Read(std::filesystem::path const& path)
{
    auto attributes = GetFileAttributesW(path.c_str());
    // A missing entry has nothing below it to follow.
    if (attributes == INVALID_FILE_ATTRIBUTES)
        return Opaque{};
    if (!(attributes & FILE_ATTRIBUTE_REPARSE_POINT))
        return std::monostate{};
    auto handle = CreateFileW(path.c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    if (handle == INVALID_HANDLE_VALUE)
        return Opaque{};
    OwnedHandle owned(handle);
    std::vector<std::byte> buffer(MAXIMUM_REPARSE_DATA_BUFFER_SIZE);
    DWORD returned = 0;
    if (!DeviceIoControl(handle, FSCTL_GET_REPARSE_POINT, nullptr, 0, buffer.data(), static_cast<DWORD>(buffer.size()),
        &returned, nullptr) || returned < mountNames)
        return Opaque{};
    ULONG tag = 0;
    std::memcpy(&tag, buffer.data(), sizeof(tag));
    // Other reparse points, such as cloud files, are ordinary folders.
    if (!IsReparseTagNameSurrogate(tag))
        return std::monostate{};
    if (tag != IO_REPARSE_TAG_SYMLINK && tag != IO_REPARSE_TAG_MOUNT_POINT)
        return Opaque{};
    auto names = tag == IO_REPARSE_TAG_SYMLINK ? linkNames : mountNames;
    USHORT offset = 0;
    USHORT length = 0;
    std::memcpy(&offset, buffer.data() + reparseNames, sizeof(offset));
    std::memcpy(&length, buffer.data() + reparseNames + sizeof(offset), sizeof(length));
    if (names + offset + length > returned)
        return Opaque{};
    std::wstring target(length / sizeof(wchar_t), L'\0');
    std::memcpy(target.data(), buffer.data() + names + offset, length);
    ULONG flags = 0;
    if (tag == IO_REPARSE_TAG_SYMLINK)
        std::memcpy(&flags, buffer.data() + mountNames, sizeof(flags));
    if (flags & relativeLink)
        return path.parent_path() / target;
    if (auto win32 = Win32Path(std::move(target)))
        return *win32;
    return Opaque{};
}
}

Engine::State::Location::Walk Engine::State::FollowLinks(Location::Walk walk)
{
    auto root = lt::storage_root_key(Utf8(walk.path.wstring()));
    while (walk.links < linkLimit)
    {
        // The walk stops at a network path, because the engine cannot see
        // behind it, and at any other path without a drive letter.
        auto letter = walk.path.root_name().wstring();
        if (letter.size() != 2 || letter[1] != L':')
            return walk;
        std::optional<std::filesystem::path> followed;
        if (auto target = DriveTarget(letter))
            followed = FullPath(*target / walk.path.relative_path());
        auto walked = walk.path.root_path();
        auto parts = walk.path.relative_path();
        for (auto part = parts.begin(); part != parts.end() && !followed; ++part)
        {
            walked /= *part;
            auto entry = Read(walked);
            if (std::holds_alternative<Opaque>(entry))
                return walk;
            if (auto target = std::get_if<std::filesystem::path>(&entry))
            {
                std::filesystem::path rest;
                for (auto next = std::next(part); next != parts.end(); ++next)
                    rest /= *next;
                followed = FullPath(*target / rest);
            }
        }
        if (!followed)
            return walk;
        walk.path = std::move(*followed);
        ++walk.links;
        // Reading on past here would touch another volume.
        if (lt::storage_root_key(Utf8(walk.path.wstring())) != root)
            return walk;
    }
    return walk;
}

std::string Engine::State::LocationKey(std::string const& folder)
{
    return RootKey(FullPath(Wide(folder)));
}

std::string Engine::State::RealPath(std::string const& path) const
{
    if (locations.empty())
        return path;
    auto full = FullPath(Wide(path));
    for (auto folder = full;; folder = folder.parent_path())
    {
        auto found = locations.find(RootKey(folder));
        if (found != locations.end() && found->second.real)
        {
            auto rest = full.wstring().substr(folder.wstring().size());
            rest.erase(0, rest.find_first_not_of(L'\\'));
            std::filesystem::path real = Wide(*found->second.real);
            return Utf8((rest.empty() ? real : real / rest).wstring());
        }
        if (folder == folder.parent_path())
            return path;
    }
}

std::string Engine::State::RootOf(std::string const& folder) const
{
    return lt::storage_root_key(RealPath(folder));
}

std::optional<std::string> Engine::State::NoticeRoot(std::string const& location) const
{
    auto folder = Wide(RealPath(location));
    if (folder.starts_with(LR"(\\?\UNC\)"))
        folder = LR"(\\)" + folder.substr(8);
    else if (folder.starts_with(LR"(\\?\)"))
        folder.erase(0, 4);
    auto path = std::filesystem::path(folder);
    path.make_preferred();
    auto root = ShareRoot(path);
    if (root.empty())
        return std::nullopt;
    return Utf8(root.wstring());
}

std::string Engine::State::NoticeKey(std::string const& location) const
{
    return RootKey(Wide(NoticeRoot(location).value_or(location)));
}

bool Engine::State::Locate(std::string const& folder)
{
    auto key = LocationKey(folder);
    auto [entry, added] = locations.try_emplace(key, Location{folder});
    if (added)
        LookUp(key);
    return entry->second.real.has_value();
}

void Engine::State::LocateFolders()
{
    std::map<std::string, std::string> wanted;
    auto want = [&wanted](std::string const& folder)
    {
        if (!folder.empty())
            wanted.try_emplace(LocationKey(folder), folder);
    };
    EachWorking([&want](Torrent const& torrent)
    {
        want(torrent.facts.savePath);
        want(torrent.facts.finalFolder);
        if (torrent.facts.marker)
            want(torrent.facts.marker->destination);
        if (torrent.waitingMove)
            want(torrent.waitingMove->destination);
    });
    for (auto const& [id, addition] : additions)
    {
        want(addition.facts.savePath);
        want(addition.facts.finalFolder);
    }
    for (auto const& moving : moves)
        want(moving.destination);
    fileWork.Each([&want](FileWork::Job const& job)
    {
        for (auto const& use : job.uses)
            want(use.folder);
    });
    // A new torrent usually saves in one of these, so its folder is known
    // before it is added.
    want(settings.AdditionFolder());
    want(settings.incompleteFolder);
    want(settings.watchDestination);
    std::erase_if(locations, [&wanted](auto const& entry)
        { return !entry.second.walk && !wanted.contains(entry.first); });
    auto now = std::chrono::steady_clock::now();
    for (auto const& [key, folder] : wanted)
    {
        auto [entry, added] = locations.try_emplace(key, Location{folder});
        if (added || (!entry->second.walk && now - entry->second.at >= lookupInterval))
            LookUp(key);
    }
    ContinueLookups();
}

void Engine::State::LookUp(std::string const& key)
{
    auto& location = locations.at(key);
    location.at = std::chrono::steady_clock::now();
    auto folder = Wide(location.folder);
    location.walk = Location::Walk{FullPath(Win32Path(folder).value_or(folder))};
}

void Engine::State::ContinueLookups()
{
    using Batch = std::vector<std::pair<std::string, Location::Walk>>;
    auto rootOf = [](Location::Walk const& walk) { return lt::storage_root_key(Utf8(walk.path.wstring())); };
    std::set<std::string> busy;
    for (auto const& [key, location] : locations)
    {
        if (location.walk && location.walk->sent)
            busy.insert(rootOf(*location.walk));
    }
    std::map<std::string, Batch> batches;
    for (auto& [key, location] : locations)
    {
        if (!location.walk || location.walk->sent)
            continue;
        auto root = rootOf(*location.walk);
        if (busy.contains(root))
            continue;
        location.walk->sent = true;
        batches[root].emplace_back(key, *location.walk);
    }
    for (auto& [root, batch] : batches)
    {
        // Kept apart from the root's volume worker by the prefix, so a
        // lookup never waits behind a copy or a drive lookup there.
        auto& worker = volumes["link " + root];
        if (!worker)
            worker = std::make_unique<Store>(wake);
        worker->Run([batch]
        {
            auto walked = batch;
            for (auto& [key, walk] : walked)
            {
                // A walk that fails ends where it had got to, and the other
                // folders of the batch go on.
                try
                {
                    walk = FollowLinks(walk);
                }
                catch (std::exception const&)
                {
                }
            }
            return walked;
        }, [this, root, rootOf, batch](std::expected<Batch, StorageOutcome> walked)
        {
            for (std::size_t index = 0; index < batch.size(); ++index)
            {
                auto entry = locations.find(batch[index].first);
                if (entry == locations.end() || !entry->second.walk)
                    continue;
                auto& location = entry->second;
                // A batch that the worker refused, as at Exit, ends each walk
                // where it had got to.
                auto walk = walked ? (*walked)[index].second : *location.walk;
                if (walked && rootOf(walk) != root && walk.links < linkLimit)
                {
                    walk.sent = false;
                    location.walk = std::move(walk);
                    continue;
                }
                location.walk.reset();
                auto real = Utf8(walk.path.wstring());
                if (location.real == real)
                    continue;
                location.real = std::move(real);
                // The drives of its old real path are not the drives of the
                // folder, or of the folders inside it, now.
                auto const& key = entry->first;
                auto inside = key.ends_with('\\') ? key : key + '\\';
                std::erase_if(drives, [&key, &inside](auto const& known)
                {
                    auto folder = LocationKey(known.first);
                    return folder == key || folder.starts_with(inside);
                });
            }
            ContinueLookups();
        });
    }
}

void Engine::State::Rekey()
{
    for (auto& [id, torrent] : torrents)
    {
        // A move gives libtorrent the root of its destination itself, and the
        // folder changes only when the move ends.
        if (torrent.restore || torrent.pathWork == PathWork::Move)
            continue;
        Isolate(id, [this, &torrent]
        {
            auto root = RootOf(torrent.facts.savePath);
            if (root == torrent.storageRoot)
                return;
            torrent.handle.set_root_key(root);
            torrent.storageRoot = std::move(root);
        });
    }
}
}
