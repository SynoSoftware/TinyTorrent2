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
    // Two torrents share a file only where a top folder or file of one holds
    // or is one of the other's, so only those compare every path, which takes
    // seconds for a torrent of many files.
    auto join = [](std::string const& folder, std::string const& part)
    {
        return Utf8((std::filesystem::path(Wide(folder)) / Wide(part)).wstring());
    };
    std::vector<std::string> tops;
    for (auto const& part : Tops(metadata->layout(),
        [this, &metadata](lt::file_index_t index) { return ContentPath(metadata->layout(), index, settings.layout); }))
        tops.push_back(join(destination, part));
    auto overlaps = [&tops, &join](Torrent const& torrent)
    {
        std::vector<std::string> roots = {torrent.facts.savePath};
        if (torrent.facts.marker)
            roots.push_back(torrent.facts.marker->destination);
        for (bool logical : {true, false})
            for (auto const& part : torrent.Tops(logical))
                for (auto const& root : roots)
                    for (auto const& top : tops)
                        if (FoldersOverlap(top, join(root, part)))
                            return true;
        return false;
    };
    std::optional<std::vector<std::filesystem::path>> wanted;
    auto visit = [this, &metadata, &destination, &overlaps, &wanted, &names](Torrent const& torrent)
    {
        auto other = torrent.restore ? torrent.restore->ti : torrent.handle.torrent_file();
        if (!other || !overlaps(torrent))
        {
            return;
        }
        if (!wanted)
            wanted = FilePaths(*metadata, destination, settings.layout);
        for (auto const& full : Footprint(torrent))
        {
            if (std::binary_search(wanted->begin(), wanted->end(), full, PathBefore))
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

// A magnet preview learns its metadata through its guarded torrent. The
// person's choices start at the default priorities once the metadata is known.
void Engine::State::UpdatePreview(Preview& preview)
{
    if (preview.handle.is_valid())
    {
        if (auto metadata = preview.handle.torrent_file())
        {
            preview.params.ti = metadata;
            preview.params.info_hashes = metadata->info_hashes();
        }
    }
    if (preview.params.ti && preview.priorities.empty())
    {
        preview.priorities = DefaultPriorities(preview.params.ti->layout(),
            settings.excludes ? settings.patterns : std::string());
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

// Whether the previewed content is already in the list and the preview has
// trackers that torrent lacks.
bool Engine::State::CanMerge(Preview const& preview) const
{
    auto duplicate = FindDuplicate(preview.InfoHashes());
    return duplicate &&
        !torrents.at(*duplicate).restore &&
        !Missing(preview.params.trackers, Urls(torrents.at(*duplicate).Trackers())).empty();
}

Json Engine::State::Describe(Preview const& preview, std::string const& destination) const
{
    auto const& params = preview.params;
    auto hashes = preview.InfoHashes();
    auto duplicate = FindDuplicate(hashes);
    return {{"preview_id", preview.previewId},
        {"name", tt::Name(params.ti ? params.ti->name() : params.name, Hashes(hashes))},
        {"size", params.ti ? params.ti->total_size() : 0}, {"file_count", params.ti ? params.ti->num_files() : 0},
        {"torrent_id", duplicate.value_or("")}, {"hashes", Hashes(hashes)}, {"trackers", params.trackers},
        {"merge_available", CanMerge(preview)}, {"metadata_ready", bool(params.ti)},
        {"error", preview.error}, {"shared_with", SharedFiles(params.ti, settings.SavePath(destination))}};
}

Json Engine::State::DescribeFilePage(Preview const& preview, int offset) const
{
    auto const& metadata = preview.params.ti;
    Json data = {{"preview_id", preview.previewId}, {"offset", offset},
        {"file_count", metadata ? metadata->num_files() : 0}, {"files", Json::array()}};
    if (metadata)
    {
        auto const& layout = metadata->layout();
        data["files"] = FilePage(*metadata, offset, [this, &preview, &layout](lt::file_index_t index, Json& file)
        {
            file["path"] = ContentPath(layout, index, settings.layout);
            file["priority"] = static_cast<std::uint8_t>(preview.priorities[static_cast<int>(index)]);
        });
    }
    return data;
}

std::expected<Listing, Outcome> Engine::State::Listed(Preview& preview)
{
    UpdatePreview(preview);
    if (!preview.params.ti)
    {
        return std::unexpected(Outcome{ErrorCode::MetadataUnavailable});
    }
    return Listing{preview.params.ti, settings.layout, preview.priorities};
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
        lt::load_torrent_limits limits;
        limits.max_decode_tokens = metadataTokens;
        return lt::load_torrent_buffer(lt::span<char const>(bytes.data(), bytes.size()), limits);
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
        UpdatePreview(*preview);
        auto merge = [this, preview, completion]
        {
            auto hashes = Hashes(preview->InfoHashes());
            for (auto& [id, existing] : previews)
            {
                if (existing.connectionId == preview->connectionId && Overlaps(Hashes(existing.InfoHashes()), hashes))
                {
                    if (preview->handle.is_valid())
                        session->remove_torrent(preview->handle);
                    Merge(existing, *preview);
                    completion({}, &existing);
                    return true;
                }
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
