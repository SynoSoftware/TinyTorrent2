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

// The torrents that already use a file this content would use at the
// destination: the listed ones, and the removed ones whose files are being
// deleted. A torrent removed with its files kept never touches them again, so
// adding the same content back right after its removal is not a conflict. A
// torrent whose names are still being read counts only with its final names,
// so the Add dialog never waits for them.
std::vector<std::string> Engine::State::SharedFiles(std::shared_ptr<lt::torrent_info const> const& metadata,
    std::string const& destination) const
{
    std::vector<std::string> names;
    if (!metadata || destination.empty())
    {
        return names;
    }
    auto wanted = FilePaths(*metadata, destination, settings.layout);
    auto visit = [this, &wanted, &names](Torrent const& torrent)
    {
        auto other = torrent.restore ? torrent.restore->ti : torrent.handle.torrent_file();
        if (!other)
        {
            return;
        }
        for (auto const& full : Footprint(torrent))
        {
            if (std::binary_search(wanted.begin(), wanted.end(), full, PathBefore))
            {
                names.push_back(other->name());
                break;
            }
        }
    };
    for (auto const& [id, torrent] : torrents)
    {
        visit(torrent);
    }
    for (auto const& [id, removal] : removed)
    {
        if (removal.mode)
        {
            visit(removal.torrent);
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
    if (existing.torrentFile.empty())
    {
        existing.torrentFile = source.torrentFile;
    }
    if (!existing.params.ti && source.params.ti)
    {
        existing.params.ti = source.params.ti;
        existing.params.comment = source.params.comment;
        existing.params.created_by = source.params.created_by;
        existing.params.creation_date = source.params.creation_date;
        if (existing.handle.is_valid())
        {
            session->remove_torrent(existing.handle);
            existing.handle = {};
        }
    }
    UpdatePreview(existing);
}

Engine::State::Preview* Engine::State::Merge(Preview const& source)
{
    auto hashes = Hashes(source.InfoHashes());
    for (auto& [id, existing] : previews)
    {
        if (existing.connectionId != source.connectionId || !Overlaps(Hashes(existing.InfoHashes()), hashes))
            continue;
        if (source.handle.is_valid() && source.handle != existing.handle)
            session->remove_torrent(source.handle);
        Merge(existing, source);
        return &existing;
    }
    return nullptr;
}

// Whether the previewed content is already in the list and the preview has
// trackers that torrent lacks.
bool Engine::State::CanMerge(Preview const& preview) const
{
    auto duplicate = FindDuplicate(preview.InfoHashes());
    return duplicate &&
        !torrents.at(*duplicate).restore &&
        !Missing(preview.params.trackers, Urls(torrents.at(*duplicate).Trackers())).empty();
}

void Engine::State::DescribePreview(Preview const& preview, std::string const& destination, Reply reply,
    int offset, int limit, std::string const& query)
{
    auto const& params = preview.params;
    auto hashes = preview.InfoHashes();
    auto duplicate = FindDuplicate(hashes);
    Json data{{"preview_id", preview.previewId},
        {"name", tt::Name(params.ti ? params.ti->name() : params.name, Hashes(hashes))},
        {"size", params.ti ? params.ti->total_size() : 0},
        {"torrent_id", duplicate.value_or("")}, {"hashes", Hashes(hashes)}, {"trackers", params.trackers},
        {"merge_available", CanMerge(preview)}, {"metadata_ready", bool(params.ti)},
        {"error", preview.error}, {"shared_with", SharedFiles(params.ti, settings.SavePath(destination))}};
    auto identity = preview.previewId;
    auto connection = preview.connectionId;
    sources.Run([metadata = params.ti, offset, limit, query, layout = settings.layout,
        patterns = settings.excludes ? settings.patterns : std::string()]() mutable
    {
    auto files = Json::array();
    int count = metadata ? metadata->num_files() : 0;
    int selectable = 0;
    int wanted = 0;
    std::int64_t fileBytes = 0;
    std::int64_t wantedBytes = 0;
    int next = std::clamp(offset, 0, count);
    limit = std::clamp(limit, 0, 500);
    if (metadata)
    {
        auto const& storage = metadata->layout();
        auto priorities = DefaultPriorities(storage, patterns);
        for (auto index : storage.file_range())
        {
            if (storage.pad_file_at(index))
                continue;
            ++selectable;
            fileBytes += storage.file_size(index);
            if (priorities[static_cast<int>(index)] != lt::dont_download)
            {
                ++wanted;
                wantedBytes += storage.file_size(index);
            }
        }
        auto needle = Wide(query);
        auto matches = [&needle](std::string const& path)
        {
            auto text = Wide(path);
            if (needle.empty())
                return true;
            for (std::size_t start = 0; start + needle.size() <= text.size(); ++start)
                if (CompareStringOrdinal(text.data() + start, static_cast<int>(needle.size()),
                    needle.data(), static_cast<int>(needle.size()), TRUE) == CSTR_EQUAL)
                    return true;
            return false;
        };
        int visited = 0;
        while (next < count && static_cast<int>(files.size()) < limit && visited++ < 4096)
        {
            auto index = lt::file_index_t(next++);
            if (storage.pad_file_at(index))
                continue;
            auto path = ContentPath(storage, index, layout);
            if (!matches(path))
                continue;
            files.push_back({{"index", static_cast<int>(index)}, {"path", std::move(path)},
                {"size", storage.file_size(index)}, {"padding", false},
                {"priority", static_cast<std::uint8_t>(priorities[static_cast<int>(index)])}});
        }
    }
    return Json{{"files", std::move(files)},
        {"file_offset", std::clamp(offset, 0, count)}, {"file_count", count},
        {"next_offset", next < count ? Json(next) : Json(nullptr)},
        {"selectable_count", selectable}, {"file_bytes", fileBytes},
        {"wanted_count", wanted}, {"wanted_bytes", wantedBytes}};
    }, [this, identity, connection, data = std::move(data), reply](std::expected<Json, StorageOutcome> page) mutable
    {
        if (!FindPreview(identity, connection))
            reply(Failure(ErrorCode::PreviewExpired));
        else if (!page)
            reply(Failure(ErrorCode::PreviewFailed, page.error().detail));
        else
        {
            data.update(*page);
            reply(Success(std::move(data)));
        }
    });
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
    sources.Run([source]
    {
        if (IsMagnet(source))
        {
            return lt::parse_magnet_uri(source);
        }
        auto bytes = Store::Read(std::filesystem::path(Wide(source)));
        return lt::load_torrent_buffer(lt::span<char const>(bytes.data(), bytes.size()));
    }, [this, preview, completion, source](std::expected<lt::add_torrent_params, StorageOutcome> parsed)
    {
        std::erase(parsing, preview);
        if (preview->cancelled)
        {
            return;
        }
        if (IsShuttingDown())
        {
            completion({ErrorCode::ShuttingDown}, nullptr);
            return;
        }
        if (!parsed)
        {
            completion(parsed.error().ToOutcome(ErrorCode::InvalidSource), nullptr);
            return;
        }
        preview->params = std::move(*parsed);
        if (!IsMagnet(source))
            preview->torrentFile = source;
        preview->params.info_hashes = preview->InfoHashes();
        auto merge = [this, preview, completion]
        {
            if (auto existing = Merge(*preview))
            {
                completion({}, existing);
                return true;
            }
            return false;
        };
        // libtorrent still holds the removed copy, so it would refuse this one.
        if (Removing(preview->InfoHashes()))
        {
            completion({ErrorCode::TorrentRemoving}, nullptr);
            return;
        }
        if (merge())
            return;
        if (!preview->params.ti && !FindDuplicate(preview->params.info_hashes))
        {
            Guard(preview->params);
            preview->params.flags &= ~lt::torrent_flags::paused;
            preview->params.save_path = Utf8((directory / L"previews").wstring());
            parsing.push_back(preview);
            sources.Run([params = preview->params, session = session.get()]
            {
                return session->add_torrent(params);
            }, [this, preview, completion, merge](std::expected<lt::torrent_handle, StorageOutcome> added)
            {
                std::erase(parsing, preview);
                if (added)
                    preview->handle = *added;
                if (preview->cancelled || IsShuttingDown())
                {
                    if (preview->handle.is_valid())
                        session->remove_torrent(preview->handle);
                    if (!preview->cancelled)
                        completion({ErrorCode::ShuttingDown}, nullptr);
                    return;
                }
                UpdatePreview(*preview);
                if (merge())
                    return;
                if (!added)
                {
                    completion(added.error().ToOutcome(ErrorCode::PreviewFailed), nullptr);
                    return;
                }
                ApplyPolicy(preview->handle);
                completion({}, &previews.emplace(preview->previewId, *preview).first->second);
            });
            return;
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
    snapshotSubscribers.erase(connectionId);
    ReleaseConnectionTest(connectionId, ConnectionPhase::Cancelled);
    ReleaseReading(connectionId, true);
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
