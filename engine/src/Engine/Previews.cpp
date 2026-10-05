#include "Engine/State.h"
#include <Windows.h>
#include <libtorrent/load_torrent.hpp>
#include <libtorrent/magnet_uri.hpp>
#include <libtorrent/torrent_info.hpp>
#include <algorithm>
#include <cstring>

namespace tiny
{
namespace
{
constexpr std::string_view magnetScheme = "magnet:";
// Bounds the previews that wait for a choice, so that a burst of requests
// cannot grow memory without limit.
constexpr std::size_t previewLimit = 256;
}

std::filesystem::path Engine::State::FullPath(std::filesystem::path const& path)
{
    return std::filesystem::absolute(path).lexically_normal();
}

// Windows compares file names without case.
bool Engine::State::PathBefore(std::filesystem::path const& left, std::filesystem::path const& right)
{
    return CompareStringOrdinal(left.c_str(), -1, right.c_str(), -1, TRUE) == CSTR_LESS_THAN;
}

// The torrents in the list that already use a file this content would use
// at the destination.
std::vector<std::string> Engine::State::SharedFiles(std::shared_ptr<lt::torrent_info const> const& metadata,
    std::string const& destination) const
{
    std::vector<std::string> names;
    if (!metadata || destination.empty())
    {
        return names;
    }
    std::filesystem::path target = Wide(destination);
    std::vector<std::filesystem::path> wanted;
    for (auto const& path : Paths(*metadata))
    {
        wanted.push_back(FullPath(target / path));
    }
    std::sort(wanted.begin(), wanted.end(), PathBefore);
    for (auto const& [id, torrent] : torrents)
    {
        auto other = torrent.handle.torrent_file();
        if (!other)
        {
            continue;
        }
        for (auto const& full : FilePaths(torrent))
        {
            if (std::binary_search(wanted.begin(), wanted.end(), full, PathBefore))
            {
                names.push_back(other->name());
                break;
            }
        }
    }
    if (HoldsFiles(metadata, destination))
    {
        if (deletion) names.push_back(deletion->names);
        else for (auto const& id : relocation->ids) names.push_back(torrents.at(id).Name());
    }
    return names;
}

// The URLs that `known` does not hold yet, each once.
std::vector<std::string> Engine::State::Missing(std::vector<std::string> const& urls,
    std::vector<std::string> known)
{
    std::vector<std::string> missing;
    for (auto const& url : urls)
    {
        if (!Contains(known, url))
        {
            known.push_back(url);
            missing.push_back(url);
        }
    }
    return missing;
}

// A magnet preview learns its metadata through its guarded torrent.
void Engine::State::UpdatePreview(Preview& preview)
{
    if (!preview.handle.is_valid())
    {
        return;
    }
    if (auto metadata = preview.handle.torrent_file())
    {
        preview.params.ti = metadata;
        preview.params.info_hashes = metadata->info_hashes();
    }
}

// A second source for content this connection already previews adds its
// trackers and metadata to that preview instead of opening another one.
void Engine::State::Merge(Preview& existing, Preview const& source)
{
    for (auto const& url : Missing(source.params.trackers, existing.params.trackers))
    {
        existing.params.trackers.push_back(url);
        if (existing.handle.is_valid())
        {
            existing.handle.add_tracker(lt::announce_entry(url));
        }
    }
    if (!existing.params.tracker_tiers.empty())
    {
        existing.params.tracker_tiers.resize(existing.params.trackers.size(), 0);
    }
    if (!existing.params.ti && source.params.ti)
    {
        existing.params.ti = source.params.ti;
        existing.params.comment = source.params.comment;
        existing.params.created_by = source.params.created_by;
        existing.params.creation_date = source.params.creation_date;
        if (existing.handle.is_valid())
        {
            existing.handle.set_metadata(source.params.ti->info_section());
        }
    }
    UpdatePreview(existing);
}

Json Engine::State::Describe(Preview const& preview, std::string const& destination) const
{
    auto const& params = preview.params;
    auto hashes = preview.InfoHashes();
    auto duplicate = Duplicate(hashes);
    bool merge = !duplicate.empty() &&
        !Missing(params.trackers, Urls(torrents.at(duplicate).handle.trackers())).empty();
    return {{"preview_id", preview.identity},
        {"name", tiny::Name(params.ti ? params.ti->name() : params.name, Hashes(hashes))},
        {"size", params.ti ? params.ti->total_size() : 0}, {"files", Files(params.ti)},
        {"duplicate", duplicate}, {"hashes", Hashes(hashes)}, {"trackers", params.trackers},
        {"merge_available", merge}, {"metadata_ready", bool(params.ti)},
        {"error", preview.error}, {"shared_with", SharedFiles(params.ti, destination)}};
}

void Engine::State::Inspect(std::string source, std::string connection, std::string destination, Reply reply)
{
    if (previews.size() + parsing.size() >= previewLimit)
    {
        reply(Failure("overloaded"));
        return;
    }
    if (!IsSource(source))
    {
        reply(Failure("invalid_source"));
        return;
    }
    // URI schemes ignore case.
    if (_strnicmp(source.c_str(), magnetScheme.data(), magnetScheme.size()) == 0)
    {
        source.replace(0, magnetScheme.size(), magnetScheme);
    }
    auto preview = std::make_shared<Preview>();
    preview->identity = Identity();
    preview->connection = std::move(connection);
    store.Run([preview, source]
    {
        if (source.starts_with(magnetScheme))
        {
            preview->params = lt::parse_magnet_uri(source);
        }
        else
        {
            auto bytes = Store::Read(std::filesystem::path(Wide(source)));
            preview->params = lt::load_torrent_buffer(
                lt::span<char const>(bytes.data(), bytes.size()));
        }
    }, [this, preview, destination = std::move(destination), reply](StorageOutcome outcome)
    {
        std::erase(parsing, preview);
        if (preview->cancelled)
        {
            return;
        }
        if (stopping)
        {
            reply(Failure("stopping"));
            return;
        }
        if (!outcome.succeeded)
        {
            reply(Failure("invalid_source", outcome.detail));
            return;
        }
        preview->params.info_hashes = preview->InfoHashes();
        auto hashes = Hashes(preview->params.info_hashes);
        for (auto& [id, existing] : previews)
        {
            if (existing.connection == preview->connection && Overlaps(Hashes(existing.InfoHashes()), hashes))
            {
                Merge(existing, *preview);
                reply(Success(Describe(existing, destination)));
                return;
            }
        }
        if (!preview->params.ti && Duplicate(preview->params.info_hashes).empty())
        {
            Guard(preview->params);
            preview->params.flags &= ~lt::torrent_flags::paused;
            preview->params.save_path = Utf8((directory / L"previews").wstring());
            lt::error_code error;
            preview->handle = session->add_torrent(preview->params, error);
            if (error)
            {
                reply(Failure("preview_failed", error.message()));
                return;
            }
        }
        previews.emplace(preview->identity, *preview);
        reply(Success(Describe(*preview, destination)));
    });
    parsing.push_back(preview);
}

// A preview belongs to the connection that opened it.
Engine::State::Preview* Engine::State::FindPreview(std::string const& id, std::string const& connection)
{
    auto found = previews.find(id);
    if (found == previews.end() || found->second.connection != connection)
    {
        return nullptr;
    }
    return &found->second;
}

// Forgets the matching previews and removes their guarded torrents.
void Engine::State::Discard(std::function<bool(Preview const&)> const& matches)
{
    std::erase_if(previews, [this, &matches](auto const& entry)
    {
        if (!matches(entry.second))
        {
            return false;
        }
        if (entry.second.handle.is_valid())
        {
            session->remove_torrent(entry.second.handle);
        }
        return true;
    });
}

void Engine::State::Disconnect(std::string const& connection)
{
    for (auto const& preview : parsing)
    {
        if (preview->connection == connection)
        {
            preview->cancelled = true;
        }
    }
    Discard([&connection](Preview const& preview) { return preview.connection == connection; });
}

void Engine::State::On(lt::metadata_failed_alert const& alert)
{
    for (auto& [id, preview] : previews)
    {
        if (preview.handle == alert.handle)
        {
            preview.error = alert.error.message();
        }
    }
}
}
