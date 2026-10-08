#include "Engine/State.h"
#include <libtorrent/load_torrent.hpp>
#include <libtorrent/magnet_uri.hpp>
#include <libtorrent/torrent_info.hpp>
#include <algorithm>
#include <cstring>

namespace tt
{
namespace
{
// Bounds the previews that wait for a choice, so that a burst of requests
// cannot grow memory without limit.
constexpr std::size_t previewLimit = 256;
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
    auto wanted = FilePaths(*metadata, destination, settings.layout);
    for (auto const& [id, torrent] : torrents)
    {
        auto other = torrent.restore ? torrent.restore->ti : torrent.handle.torrent_file();
        if (!other)
        {
            continue;
        }
        auto paths = FilePaths(torrent);
        auto physical = FilePaths(torrent, {}, false);
        paths.insert(paths.end(), physical.begin(), physical.end());
        for (auto const& full : paths)
        {
            if (std::binary_search(wanted.begin(), wanted.end(), full, PathBefore))
            {
                names.push_back(other->name());
                break;
            }
        }
    }
    auto holders = Holders(metadata, destination);
    names.insert(names.end(), holders.begin(), holders.end());
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

// Whether the previewed content is already in the list and the preview has
// trackers that torrent lacks.
bool Engine::State::CanMerge(Preview const& preview) const
{
    auto duplicate = FindDuplicate(preview.InfoHashes());
    return !duplicate.empty() &&
        !torrents.at(duplicate).restore &&
        !Missing(preview.params.trackers, Urls(torrents.at(duplicate).handle.trackers())).empty();
}

Json Engine::State::Describe(Preview const& preview, std::string const& destination) const
{
    auto const& params = preview.params;
    auto hashes = preview.InfoHashes();
    auto duplicate = FindDuplicate(hashes);
    auto files = Files(params.ti);
    if (params.ti)
    {
        auto priorities = DefaultPriorities(params.ti->layout(), settings.excludes ? settings.patterns : std::string());
        for (auto& file : files)
        {
            auto index = lt::file_index_t(file.at("index").get<int>());
            file["path"] = ContentPath(params.ti->layout(), index, settings.layout);
            file["priority"] = static_cast<std::uint8_t>(priorities[static_cast<int>(index)]);
        }
    }
    return {{"preview_id", preview.previewId},
        {"name", tt::Name(params.ti ? params.ti->name() : params.name, Hashes(hashes))},
        {"size", params.ti ? params.ti->total_size() : 0}, {"files", std::move(files)},
        {"torrent_id", duplicate}, {"hashes", Hashes(hashes)}, {"trackers", params.trackers},
        {"merge_available", CanMerge(preview)}, {"metadata_ready", bool(params.ti)},
        {"error", preview.error}, {"shared_with", SharedFiles(params.ti, settings.SavePath(destination))}};
}

void Engine::State::Inspect(std::string source, std::string connectionId,
    std::function<void(Outcome, Preview*)> completion)
{
    if (previews.size() + parsing.size() >= previewLimit)
    {
        completion({ErrorCode::Overloaded}, nullptr);
        return;
    }
    if (!IsSource(source))
    {
        completion({ErrorCode::InvalidSource}, nullptr);
        return;
    }
    NormaliseMagnet(source);
    auto preview = std::make_shared<Preview>();
    preview->previewId = NewId();
    preview->connectionId = std::move(connectionId);
    sources.Run([preview, source]
    {
        if (IsMagnet(source))
        {
            preview->params = lt::parse_magnet_uri(source);
        }
        else
        {
            auto bytes = Store::Read(std::filesystem::path(Wide(source)));
            preview->params = lt::load_torrent_buffer(
                lt::span<char const>(bytes.data(), bytes.size()));
        }
    }, [this, preview, completion](StorageOutcome outcome)
    {
        std::erase(parsing, preview);
        if (preview->cancelled)
        {
            return;
        }
        if (shuttingDown)
        {
            completion({ErrorCode::ShuttingDown}, nullptr);
            return;
        }
        if (!outcome.succeeded)
        {
            completion({ErrorCode::InvalidSource, outcome.detail}, nullptr);
            return;
        }
        preview->params.info_hashes = preview->InfoHashes();
        preview->params.storage_mode = settings.preallocates ? lt::storage_mode_allocate : lt::storage_mode_sparse;
        auto hashes = Hashes(preview->params.info_hashes);
        for (auto& [id, existing] : previews)
        {
            if (existing.connectionId == preview->connectionId && Overlaps(Hashes(existing.InfoHashes()), hashes))
            {
                Merge(existing, *preview);
                completion({}, &existing);
                return;
            }
        }
        if (!preview->params.ti && FindDuplicate(preview->params.info_hashes).empty())
        {
            Guard(preview->params);
            preview->params.flags &= ~lt::torrent_flags::paused;
            preview->params.save_path = Utf8((directory / L"previews").wstring());
            lt::error_code error;
            preview->handle = session->add_torrent(preview->params, error);
            if (error)
            {
                completion({ErrorCode::PreviewFailed, error.message()}, nullptr);
                return;
            }
            ApplyPolicy(preview->handle);
        }
        completion({}, &previews.emplace(preview->previewId, *preview).first->second);
    });
    parsing.push_back(preview);
}

// A preview belongs to the connection that opened it.
Engine::State::Preview* Engine::State::FindPreview(std::string const& previewId, std::string const& connectionId)
{
    auto found = previews.find(previewId);
    if (found == previews.end() || found->second.connectionId != connectionId)
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

void Engine::State::Disconnect(std::string const& connectionId)
{
    ReleaseConnectionTest(connectionId, true);
    for (auto const& preview : parsing)
    {
        if (preview->connectionId == connectionId)
        {
            preview->cancelled = true;
        }
    }
    Discard([&connectionId](Preview const& preview) { return preview.connectionId == connectionId; });
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
