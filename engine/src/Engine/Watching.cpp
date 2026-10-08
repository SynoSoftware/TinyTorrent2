#include "Engine/State.h"
#include <Windows.h>
#include <algorithm>

namespace tt
{
void Engine::State::WatchFolder()
{
    auto now = std::chrono::steady_clock::now();
    if (!settings.watches || scanningWatch || addingWatch || watchedSources.size() >= torrentLimit ||
        now - watchAt < std::chrono::seconds(5))
        return;
    watchAt = now;
    scanningWatch = true;
    auto found = std::make_shared<std::map<std::string, std::string>>();
    auto folder = settings.watchPath;
    auto recursive = settings.watchesRecursively;
    sources.Run([found, folder, recursive]
    {
        auto inspect = [found](std::filesystem::directory_entry const& entry)
        {
            if (!entry.is_regular_file() || entry.is_symlink() ||
                CompareStringOrdinal(entry.path().extension().c_str(), -1, L".torrent", -1, TRUE) != CSTR_EQUAL)
                return;
            auto path = std::filesystem::absolute(entry.path()).lexically_normal().wstring();
            CharLowerBuffW(path.data(), static_cast<DWORD>(path.size()));
            auto stamp = std::to_string(entry.file_size()) + ":" +
                std::to_string(entry.last_write_time().time_since_epoch().count());
            found->emplace(Utf8(path), std::move(stamp));
        };
        auto options = std::filesystem::directory_options::skip_permission_denied;
        std::size_t visited = 0;
        if (recursive)
        {
            for (auto const& entry : std::filesystem::recursive_directory_iterator(Wide(folder), options))
            {
                if (++visited > torrentLimit)
                    break;
                inspect(entry);
            }
        }
        else
        {
            for (auto const& entry : std::filesystem::directory_iterator(Wide(folder), options))
            {
                if (++visited > torrentLimit)
                    break;
                inspect(entry);
            }
        }
    }, [this, found, folder, recursive](StorageOutcome outcome)
    {
        scanningWatch = false;
        if (shuttingDown || !settings.watches || settings.watchPath != folder || settings.watchesRecursively != recursive)
            return;
        if (!outcome.succeeded)
        {
            if (!watchFailed)
                Notify(NoticeKind::AddFailed, folder, outcome.detail);
            watchFailed = true;
            return;
        }
        watchFailed = false;
        auto now = std::chrono::steady_clock::now();
        std::erase_if(watchedFiles, [&found](auto const& file) { return !found->contains(file.first); });
        for (auto const& [source, stamp] : *found)
        {
            auto saved = watchedSources.find(source);
            if (saved != watchedSources.end() && saved->second == stamp)
                continue;
            auto [candidate, fresh] = watchedFiles.try_emplace(source, WatchedFile{stamp, now + std::chrono::seconds(5)});
            if (!fresh && candidate->second.stamp != stamp)
                candidate->second = {stamp, now + std::chrono::seconds(5)};
            if (now < candidate->second.readyAt || addingWatch || FilesBusy())
                continue;
            addingWatch = true;
            AddSource(source, [this, source, stamp](Outcome outcome, Added added)
            {
                if (outcome.error)
                {
                    addingWatch = false;
                    auto file = watchedFiles.find(source);
                    if (file != watchedFiles.end() && file->second.stamp == stamp)
                    {
                        file->second.readyAt = std::chrono::steady_clock::now() + std::chrono::seconds(30);
                        if (!file->second.reported)
                            Notify(NoticeKind::AddFailed, source, outcome.detail);
                        file->second.reported = true;
                    }
                    return;
                }
                RecordWatch(source, stamp, [this, source, added](Outcome outcome)
                {
                    addingWatch = false;
                    if (outcome.error)
                        log.Write("watch", "", "storage_failed");
                    else if (auto found = torrents.find(added.torrentId);
                        added.kind == AdditionKind::New && found != torrents.end() && !found->second.deleted)
                        Notify(NoticeKind::Added, found->second);
                });
            }, settings.watchDestination, stamp);
            break;
        }
    });
}

void Engine::State::RecordWatch(std::string source, std::string stamp, std::function<void(Outcome)> completion)
{
    if (!changes.Queue([this, source, stamp, completion]
    {
        auto found = watchedSources.find(source);
        if (found != watchedSources.end() && found->second == stamp)
        {
            completion({});
            return;
        }
        auto document = Saved();
        document.watchedSources[source] = stamp;
        changes.Commit(document.ToJson(), [this, source, stamp, completion](StorageOutcome outcome)
        {
            if (!outcome.succeeded)
            {
                completion({ErrorCode::StorageFailed, outcome.detail});
                return;
            }
            watchedSources[source] = stamp;
            completion({});
        });
    }))
        completion({ErrorCode::Overloaded});
}
}
