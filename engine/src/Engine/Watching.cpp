#include "Engine/State.h"
#include <Windows.h>
#include <algorithm>

namespace tt
{
void Engine::State::WatchFolder()
{
    auto now = std::chrono::steady_clock::now();
    if (!settings.watches || watchPhase != WatchPhase::Idle ||
        now - watchAt < std::chrono::seconds(5))
        return;
    watchAt = now;
    watchPhase = WatchPhase::Scanning;
    auto found = std::make_shared<std::map<std::string, std::string, PathLess>>();
    auto folder = settings.watchPath;
    auto recursive = settings.watchesRecursively;
    // A result applies only to the watch that produced it.
    auto isCurrent = [this, folder, recursive]
    {
        return !IsShuttingDown() && settings.watches && settings.watchPath == folder &&
            settings.watchesRecursively == recursive;
    };
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
    }, [this, found, folder, isCurrent](StorageOutcome outcome)
    {
        watchPhase = WatchPhase::Idle;
        // A busy store is not a problem with the folder; the next scan retries.
        if (!isCurrent() || outcome.failure == StorageFailure::Overloaded)
            return;
        if (outcome.failure || found->size() > torrentLimit)
        {
            if (!watchFailed)
                Notify(NoticeKind::AddFailed, folder, outcome.detail, {},
                    outcome.failure ? "" : "watch_limit");
            watchFailed = true;
            return;
        }
        watchFailed = false;
        auto absent = [found](auto const& file) { return !found->contains(file.first); };
        if (std::any_of(watchedSources.begin(), watchedSources.end(), absent))
        {
            watchPhase = WatchPhase::Scanning;
            if (!changes.Queue([this, absent, folder, isCurrent]
            {
                if (!isCurrent())
                {
                    watchPhase = WatchPhase::Idle;
                    return;
                }
                auto document = Saved();
                std::erase_if(document.watchedSources, absent);
                changes.Commit(document.ToJson(), [this, folder, retained = document.watchedSources](Outcome outcome)
                {
                    watchPhase = WatchPhase::Idle;
                    if (outcome.error == ErrorCode::Overloaded)
                        return;
                    if (outcome.error)
                    {
                        if (!watchSaveFailed)
                            Notify(NoticeKind::AddFailed, folder, outcome.detail, {}, ToString(*outcome.error));
                        watchSaveFailed = true;
                        return;
                    }
                    watchedSources = retained;
                    watchSaveFailed = false;
                });
            }))
                watchPhase = WatchPhase::Idle;
            return;
        }
        watchSaveFailed = false;
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
            if (now < candidate->second.readyAt || FilesBusy())
                continue;
            watchPhase = WatchPhase::Adding;
            AddSource(source, [this, source, stamp](Outcome outcome, Added added)
            {
                watchPhase = WatchPhase::Idle;
                if (outcome.error)
                {
                    auto file = watchedFiles.find(source);
                    if (file != watchedFiles.end() && file->second.stamp == stamp)
                    {
                        file->second.readyAt = std::chrono::steady_clock::now() + std::chrono::seconds(30);
                        if (*outcome.error != ErrorCode::FilesBusy && *outcome.error != ErrorCode::Overloaded &&
                            !file->second.reported)
                        {
                            Notify(NoticeKind::AddFailed, source, outcome.detail);
                            file->second.reported = true;
                        }
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
