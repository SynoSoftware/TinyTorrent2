#include "Engine/State.h"
#include <Windows.h>
#include <algorithm>

namespace tt
{
void Engine::State::WatchFolder()
{
    auto now = std::chrono::steady_clock::now();
    if (!settings.watches || scanningWatch || addingWatch ||
        now - watchAt < std::chrono::seconds(5))
        return;
    watchAt = now;
    scanningWatch = true;
    auto found = std::make_shared<std::map<std::string, std::string, PathLess>>();
    auto folder = settings.watchPath;
    auto recursive = settings.watchesRecursively;
    sources.Run([found, folder, recursive]
    {
        auto inspect = [found](std::filesystem::directory_entry const& entry)
        {
            if (!entry.is_regular_file() || entry.is_symlink() ||
                CompareStringOrdinal(entry.path().extension().c_str(), -1, L".torrent", -1, TRUE) != CSTR_EQUAL)
                return;
            auto path = FullPath(entry.path());
            auto stamp = std::to_string(entry.file_size()) + ":" +
                std::to_string(entry.last_write_time().time_since_epoch().count());
            found->emplace(Utf8(path.wstring()), std::move(stamp));
        };
        if (recursive)
        {
            for (auto const& entry : std::filesystem::recursive_directory_iterator(Wide(folder)))
            {
                inspect(entry);
                if (found->size() > torrentLimit)
                    break;
            }
        }
        else
        {
            for (auto const& entry : std::filesystem::directory_iterator(Wide(folder)))
            {
                inspect(entry);
                if (found->size() > torrentLimit)
                    break;
            }
        }
    }, [this, found, folder, recursive](StorageOutcome outcome)
    {
        scanningWatch = false;
        if (shuttingDown || !settings.watches || settings.watchPath != folder || settings.watchesRecursively != recursive)
            return;
        if (!outcome.succeeded || found->size() > torrentLimit)
        {
            if (!watchFailed)
                Notify(NoticeKind::AddFailed, folder, outcome.detail, {},
                    outcome.succeeded ? "watch_limit" : "");
            watchFailed = true;
            return;
        }
        watchFailed = false;
        auto absent = [found](auto const& file) { return !found->contains(file.first); };
        if (std::any_of(watchedSources.begin(), watchedSources.end(), absent))
        {
            scanningWatch = true;
            if (!changes.Queue([this, absent, folder]
            {
                auto document = Saved();
                std::erase_if(document.watchedSources, absent);
                changes.Commit(document.ToJson(), [this, folder, retained = document.watchedSources](StorageOutcome outcome)
                {
                    scanningWatch = false;
                    if (outcome.succeeded)
                        watchedSources = retained;
                    else
                        Notify(NoticeKind::AddFailed, folder, outcome.detail);
                });
            }))
                scanningWatch = false;
            return;
        }
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
                addingWatch = false;
                if (outcome.error)
                {
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
                if (auto found = torrents.find(added.torrentId);
                    added.kind == AdditionKind::New && found != torrents.end() && !found->second.deleted)
                    Notify(NoticeKind::Added, found->second);
            }, settings.watchDestination, stamp);
            break;
        }
    });
}
}
