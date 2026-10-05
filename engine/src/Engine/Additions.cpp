#include "Engine/State.h"
#include <libtorrent/torrent_info.hpp>
#include <algorithm>
#include <chrono>

namespace tiny
{
// Writes no payload until ApplyIntent applies the saved file choices.
void Engine::State::Guard(lt::add_torrent_params& params)
{
    params.flags &= ~(lt::torrent_flags::auto_managed | lt::torrent_flags::share_mode |
        lt::torrent_flags::seed_mode);
    params.flags |= lt::torrent_flags::default_dont_download | lt::torrent_flags::duplicate_is_error;
    params.piece_priorities.clear();
    params.file_priorities.assign(params.ti ? params.ti->num_files() : 0, lt::dont_download);
}

// The person's file priorities, with defaults supplied and padding unwanted.
// A magnet without metadata keeps the all-files choice until metadata arrives.
// Returns nothing when the choice is invalid.
std::optional<std::vector<lt::download_priority_t>> Engine::State::Priorities(
    std::vector<lt::download_priority_t> chosen, std::shared_ptr<lt::torrent_info const> const& metadata)
{
    if (!metadata)
    {
        return std::vector<lt::download_priority_t>{};
    }
    auto const& files = metadata->layout();
    if (chosen.empty())
    {
        for (auto index : files.file_range())
        {
            chosen.push_back(DefaultPriority(files, index));
        }
    }
    if (chosen.size() != static_cast<std::size_t>(files.num_files()))
    {
        return std::nullopt;
    }
    for (auto index : files.file_range())
    {
        if (files.pad_file_at(index))
        {
            chosen[static_cast<int>(index)] = lt::dont_download;
        }
    }
    for (auto priority : chosen)
    {
        if (!IsChoice(priority))
        {
            return std::nullopt;
        }
    }
    return chosen;
}

// The priorities a person can choose for a file: skip, low, normal or high.
bool Engine::State::IsChoice(lt::download_priority_t priority)
{
    return priority == lt::dont_download || priority == lt::low_priority ||
        priority == lt::default_priority || priority == lt::top_priority;
}

// Starts adding the previewed content. The preview ends here, and its
// guarded torrent, if any, becomes the new torrent.
void Engine::State::Add(Preview& preview, std::string const& destination,
    std::vector<lt::download_priority_t> priorities, bool paused, Reply reply)
{
    UpdatePreview(preview);
    if (FilesBusy())
    {
        reply(Failure("files_busy"));
        return;
    }
    auto duplicate = Duplicate(preview.InfoHashes());
    if (!duplicate.empty())
    {
        reply(Success({{"torrent_id", duplicate}, {"duplicate", true}}));
        return;
    }
    if (!IsAbsolute(destination))
    {
        reply(Failure("invalid_destination"));
        return;
    }
    auto chosen = Priorities(std::move(priorities), preview.params.ti);
    if (!chosen || (preview.params.ti && std::none_of(chosen->begin(), chosen->end(),
        [](auto priority) { return priority != lt::dont_download; })))
    {
        reply(Failure("invalid_priorities"));
        return;
    }
    Addition addition;
    addition.identity = Identity();
    addition.params = preview.params;
    addition.params.save_path = destination;
    Guard(addition.params);
    addition.params.flags |= lt::torrent_flags::paused;
    addition.facts.savePath = destination;
    addition.facts.intent = paused ? Intent::Paused : Intent::Resumed;
    addition.facts.added = std::chrono::system_clock::to_time_t(std::chrono::system_clock::now());
    addition.facts.priorities = std::move(*chosen);
    addition.facts.hashes = Hashes(preview.InfoHashes());
    addition.reply = std::move(reply);
    auto& pending = additions.emplace(addition.identity, std::move(addition)).first->second;
    if (preview.handle.is_valid())
    {
        pending.handle = preview.handle;
        pending.phase = AdditionPhase::Moving;
        pending.handle.pause();
        pending.handle.move_storage(destination, lt::move_flags_t::reset_save_path);
    }
    else
    {
        pending.params.userdata = &pending;
        session->async_add_torrent(pending.params);
    }
    previews.erase(previews.find(preview.identity));
}

// Writes the resume file before membership lists the torrent, because
// startup fails when a listed torrent has no resume file.
void Engine::State::SaveAddition(std::string id, lt::torrent_handle handle)
{
    auto& addition = additions.at(id);
    addition.handle = handle;
    addition.phase = AdditionPhase::Saving;
    auto params = addition.params;
    params.userdata = lt::client_data_t{};
    WriteResume(id, std::move(params), [this, id](StorageOutcome outcome)
    {
        if (!outcome.succeeded)
        {
            diagnostics.Write("add", id, "storage_failed");
            Abandon(id, Failure("storage_failed", outcome.detail));
            return;
        }
        if (!changes.Queue([this, id] { CommitAddition(id); }))
        {
            Abandon(id, Failure("overloaded"));
        }
    });
}

void Engine::State::CommitAddition(std::string const& id)
{
    auto document = Saved();
    document.torrents.emplace(id, additions.at(id).facts);
    document.queueOrder.push_back(id);
    changes.Commit(document.ToJson(), [this, id](StorageOutcome outcome)
    {
        if (!outcome.succeeded)
        {
            Abandon(id, Failure("storage_failed", outcome.detail));
            return;
        }
        auto found = additions.find(id);
        auto& torrent = Install(id, found->second.handle, found->second.facts, found->second.params);
        queueOrder.push_back(id);
        torrent.ApplyIntent();
        diagnostics.Write("add", id, "saved");
        auto reply = std::move(found->second.reply);
        additions.erase(found);
        reply(Success({{"torrent_id", id}, {"duplicate", false}}));
    });
}

// Ends an addition that will not be saved: removes its torrent and
// replies why.
void Engine::State::Abandon(std::string id, Json response)
{
    auto found = additions.find(id);
    auto reply = std::move(found->second.reply);
    if (found->second.handle.is_valid())
    {
        session->remove_torrent(found->second.handle);
    }
    // Membership never lists this torrent, so nothing else removes its resume
    // file.
    if (found->second.phase == AdditionPhase::Saving)
    {
        store.Run([file = ResumeFile(id)] { std::filesystem::remove(file); },
            [this, id](StorageOutcome removed)
        {
            if (!removed.succeeded)
            {
                diagnostics.Write("add", id, "metadata_cleanup_failed");
            }
        });
    }
    additions.erase(found);
    reply(std::move(response));
}

std::string Engine::State::MovingAddition(lt::torrent_handle const& handle) const
{
    for (auto const& [id, addition] : additions)
    {
        if (addition.phase == AdditionPhase::Moving && addition.handle == handle)
        {
            return id;
        }
    }
    return {};
}

void Engine::State::On(lt::add_torrent_alert const& alert)
{
    auto pointer = alert.params.userdata.get<Addition>();
    if (!pointer)
    {
        return;
    }
    auto found = std::find_if(additions.begin(), additions.end(),
        [pointer](auto const& entry) { return &entry.second == pointer; });
    if (found == additions.end() || found->second.handle.is_valid())
    {
        return;
    }
    if (!alert.error)
    {
        SaveAddition(found->first, alert.handle);
        return;
    }
    auto duplicate = Duplicate(alert.params.info_hashes);
    Abandon(found->first, duplicate.empty() ? Failure("add_failed", alert.error.message()) :
        Success({{"torrent_id", duplicate}, {"duplicate", true}}));
}

void Engine::State::On(lt::storage_moved_alert const& alert)
{
    if (relocation)
    {
        FinishMove(alert.handle, std::nullopt);
        return;
    }
    auto id = MovingAddition(alert.handle);
    if (!id.empty())
    {
        SaveAddition(id, alert.handle);
    }
}

void Engine::State::On(lt::storage_moved_failed_alert const& alert)
{
    if (relocation)
    {
        auto kind = alert.error == boost::system::errc::file_exists ?
            ProblemKind::DestinationExists : ProblemKind::MoveFailed;
        FinishMove(alert.handle, Problem{kind, alert.error.message()});
        return;
    }
    auto id = MovingAddition(alert.handle);
    if (!id.empty())
    {
        Abandon(id, Failure("add_failed", alert.error.message()));
    }
}
}
