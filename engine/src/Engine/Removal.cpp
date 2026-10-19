#include "Engine/State.h"
#include <Windows.h>
#include <Shellapi.h>
#include <ShlObj.h>
#include <algorithm>
#include <system_error>

namespace tt
{
// Removes the torrents from the list. With a mode it also deletes their
// files. A torrent that CanRemove refuses, or whose deletion waits for other
// torrents' names, leaves the list at once and is removed later. With a
// torrentFiles mode it deletes the .torrent files they were added from once
// the list no longer holds them.
void Engine::State::Remove(std::vector<std::string> const& ids,
    std::function<void(Outcome, std::optional<std::size_t> kept)> completion,
    std::optional<DeletionMode> mode, std::optional<DeletionMode> torrentFiles)
{
    auto namesReady = !mode || NamesReady();
    std::vector<std::string> ready;
    std::vector<std::string> later;
    for (auto const& id : ids)
    {
        auto const& torrent = torrents.at(id);
        bool removable = namesReady && CanRemove(torrent, mode);
        (removable ? ready : later).push_back(id);
    }
    std::optional<std::size_t> kept = 0;
    auto scope = mode && !ready.empty() ? FileScope(ready) : Scope{};
    if (mode)
    {
        kept = namesReady ? std::optional(later.empty() ? scope.kept.size() : FileScope(ids).kept.size()) :
            std::nullopt;
    }
    auto deletion = PrepareDeletion(ready, mode, std::move(scope));
    std::vector<std::filesystem::path> sources;
    std::string sourceNames;
    if (torrentFiles)
    {
        for (auto const& id : ids)
        {
            auto const& torrent = torrents.at(id);
            if (torrent.facts.torrentFile.empty())
                continue;
            sources.push_back(Wide(torrent.facts.torrentFile));
            sourceNames += (sourceNames.empty() ? "" : ", ") + torrent.Name();
        }
    }
    auto removed = [&ids](std::string const& id) { return Contains(ids, id); };
    auto document = Saved();
    std::erase_if(document.torrents, [&removed](auto const& entry) { return removed(entry.first); });
    std::erase_if(document.queueOrder, removed);
    auto order = document.queueOrder;
    changes.Commit([document = std::move(document)] { return document.ToJson(); },
        [this, ready, later, order = std::move(order), deletion, kept, completion, mode, torrentFiles,
            sources = std::move(sources), sourceNames = std::move(sourceNames)](Outcome outcome)
    {
        if (outcome.error)
        {
            if (deletion != deletions.end())
            {
                deletions.erase(deletion);
                ContinueDeletion();
            }
            completion(std::move(outcome), kept);
            return;
        }
        queueOrder = order;
        RemoveHandles(ready, deletion);
        for (auto const& id : later)
        {
            auto& torrent = torrents.at(id);
            torrent.deleted = true;
            torrent.deletionMode = mode;
            torrent.ApplyIntent();
        }
        if (!sources.empty())
            DeleteTorrentFiles(sources, *torrentFiles, sourceNames);
        ContinueMove();
        completion({}, kept);
    });
}

std::list<Engine::State::Deletion>::iterator Engine::State::PrepareDeletion(std::vector<std::string> const& ids,
    std::optional<DeletionMode> mode, Scope scope)
{
    if (ids.empty())
    {
        return deletions.end();
    }
    Deletion deleting;
    deleting.mode = mode;
    if (mode)
    {
        deleting.holds = std::move(scope.holds);
        for (auto const& file : scope.files)
        {
            if (!std::binary_search(scope.kept.begin(), scope.kept.end(), file, PathBefore))
                deleting.files.push_back(file);
        }
    }
    for (auto const& id : ids)
    {
        auto const& torrent = torrents.at(id);
        if (mode)
            deleting.roots.push_back(FullPath(Wide(torrent.facts.savePath)));
        else
        {
            auto held = Footprint(torrent);
            deleting.holds.insert(deleting.holds.end(), held.begin(), held.end());
        }
        if (!deleting.names.empty())
        {
            deleting.names += ", ";
        }
        deleting.names += torrent.Name();
    }
    std::sort(deleting.holds.begin(), deleting.holds.end(), PathBefore);
    deleting.holds.erase(std::unique(deleting.holds.begin(), deleting.holds.end(), SamePath), deleting.holds.end());
    return deletions.insert(deletions.end(), std::move(deleting));
}

// Membership has already committed; cleanup failure must not restore it.
void Engine::State::RemoveHandles(std::vector<std::string> const& ids, std::list<Deletion>::iterator deletion)
{
    if (ids.empty())
    {
        return;
    }
    std::vector<std::filesystem::path> files;
    for (auto const& id : ids)
    {
        auto found = torrents.find(id);
        if (found->second.priorityCompletion)
        {
            std::exchange(found->second.priorityCompletion, nullptr)({ErrorCode::TorrentRemoved});
        }
        if (reading && reading->torrentId == id)
        {
            FailReading(Failure(ErrorCode::TorrentRemoved));
        }
        if (!found->second.restore)
        {
            if (deletion->mode)
            {
                session->remove_torrent(found->second.handle, lt::session::delete_partfile);
            }
            else
            {
                session->remove_torrent(found->second.handle);
            }
            deletion->waiting.push_back(found->second.handle);
            handles.erase(found->second.handle);
            seedQueries.erase(found->second.handle);
        }
        torrents.erase(found);
        files.push_back(ResumeFile(id));
    }
    deletion->phase = DeletionPhase::Waiting;
    ContinueDeletion();
    store.Run([files]
    {
        std::error_code failure;
        for (auto const& file : files)
        {
            std::error_code error;
            std::filesystem::remove(file, error);
            if (error && !failure)
                failure = error;
        }
        if (failure)
            throw std::system_error(failure);
    }, [this](StorageOutcome removed)
    {
        if (removed.failure)
        {
            log.Write("remove", "", "metadata_cleanup_failed");
        }
    });
}

// A removed torrent leaves the session once libtorrent has finished its own
// move or rename, which it cannot stop halfway. Deleting its files also waits
// until no addition runs, because FileScope cannot see the files an addition
// shares.
bool Engine::State::CanRemove(Torrent const& torrent, std::optional<DeletionMode> mode) const
{
    return !torrent.FilesBusy() && (!mode || additions.empty());
}

void Engine::State::RemoveDeferred()
{
    // A pending membership change may have kept files for these handles.
    if (!changes.IsIdle())
    {
        return;
    }
    // Only a deletion needs every torrent's names, to find the files it keeps.
    auto namesReady = NamesReady();
    for (auto mode : {std::optional(DeletionMode::Recycle), std::optional(DeletionMode::Permanent),
        std::optional<DeletionMode>()})
    {
        if (mode && !namesReady)
            continue;
        std::vector<std::string> ready;
        for (auto const& [id, torrent] : torrents)
            if (torrent.deleted && torrent.deletionMode == mode && CanRemove(torrent, mode))
                ready.push_back(id);
        if (!ready.empty())
            RemoveHandles(ready, PrepareDeletion(ready, mode, mode ? FileScope(ready) : Scope{}));
    }
}

void Engine::State::ContinueDeletion()
{
    // libtorrent destroys a removed torrent only after it releases the files.
    // This also releases holds when disk-stop fails without posting an alert.
    for (auto& deletion : deletions)
    {
        if (deletion.phase == DeletionPhase::Unknown ||
            (!deletion.mode && deletion.phase == DeletionPhase::Waiting))
        {
            std::erase_if(deletion.waiting, [](auto const& handle) { return !handle.is_valid(); });
        }
    }
    auto released = [](Deletion const& deletion)
    {
        return deletion.waiting.empty() &&
            (deletion.phase == DeletionPhase::Waiting || deletion.phase == DeletionPhase::Unknown);
    };
    // A removal that keeps the files has nothing to delete once they are released.
    std::erase_if(deletions, [&released](auto const& deletion) { return !deletion.mode && released(deletion); });
    for (auto deletion = deletions.begin(); deletion != deletions.end(); ++deletion)
    {
        if (released(*deletion))
        {
            // Another removed handle can still flush writes to a shared file.
            bool held = std::any_of(deletions.begin(), deletions.end(), [&deletion](auto const& other)
            {
                return (other.phase == DeletionPhase::Saving || !other.waiting.empty()) &&
                    std::any_of(deletion->files.begin(), deletion->files.end(), [&other](auto const& file)
                    {
                        return std::binary_search(other.holds.begin(), other.holds.end(), file, PathBefore);
                    });
            });
            if (!held)
            {
                Delete(deletion);
            }
        }
    }
}

namespace
{
// Sends the files to the Recycle Bin and throws the first failure. A file that
// is already gone counts as deleted.
void Recycle(std::vector<std::filesystem::path> const& files)
{
    auto initialized = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    IFileOperation* operation = nullptr;
    auto result = FAILED(initialized) ? initialized : CoCreateInstance(CLSID_FileOperation, nullptr,
        CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&operation));
    if (SUCCEEDED(result))
        result = operation->SetOperationFlags(FOFX_RECYCLEONDELETE | FOFX_EARLYFAILURE |
            FOF_NOERRORUI | FOF_SILENT | FOF_NOCONFIRMATION | FOF_NO_CONNECTED_ELEMENTS);
    for (auto const& file : files)
    {
        if (FAILED(result))
            break;
        std::error_code error;
        auto type = std::filesystem::symlink_status(file, error).type();
        if (type == std::filesystem::file_type::not_found)
            continue;
        if (error || type == std::filesystem::file_type::directory)
        {
            result = E_FAIL;
            break;
        }
        IShellItem* item = nullptr;
        result = SHCreateItemFromParsingName(file.c_str(), nullptr, IID_PPV_ARGS(&item));
        if (SUCCEEDED(result))
            result = operation->DeleteItem(item, nullptr);
        if (item)
            item->Release();
    }
    if (SUCCEEDED(result))
        result = operation->PerformOperations();
    BOOL aborted = FALSE;
    if (SUCCEEDED(result))
        result = operation->GetAnyOperationsAborted(&aborted);
    if (operation)
        operation->Release();
    if (SUCCEEDED(initialized))
        CoUninitialize();
    if (FAILED(result))
        throw std::system_error(
            HRESULT_FACILITY(result) == FACILITY_WIN32 ? HRESULT_CODE(result) : result,
            std::system_category(), "HRESULT " + std::to_string(result));
    if (aborted)
        throw std::runtime_error("recycle_failed");
}
}

void Engine::State::Erase(std::vector<std::filesystem::path> const& files, DeletionMode mode,
    std::vector<std::filesystem::path> const& roots)
{
    std::exception_ptr failure;
    try
    {
        if (mode == DeletionMode::Recycle)
            Recycle(files);
        else
            Payload::Delete(files);
    }
    catch (...)
    {
        failure = std::current_exception();
    }
    std::set<std::filesystem::path, decltype(&PathBefore)> parents(PathBefore);
    for (auto const& file : files)
        parents.insert(file.parent_path());
    for (auto const& parent : parents)
    {
        for (auto const& root : roots)
        {
            auto relative = parent.lexically_relative(root);
            if (relative.empty() || *relative.begin() == L"..")
            {
                continue;
            }
            for (auto folder = parent; !SamePath(folder, root); folder = folder.parent_path())
            {
                std::error_code ignored;
                if (!std::filesystem::remove(folder, ignored))
                {
                    break;
                }
            }
        }
    }
    if (failure)
    {
        std::rethrow_exception(failure);
    }
}

void Engine::State::Delete(std::list<Deletion>::iterator deletion)
{
    auto deleting = *deletion;
    deletion->phase = DeletionPhase::Deleting;
    payload.Run([deleting]
    {
        Erase(deleting.files, *deleting.mode, deleting.roots);
    }, [this, deletion](StorageOutcome outcome)
    {
        if (outcome.failure)
        {
            ReportDeletion(outcome, deletion->names, deletion->roots.front());
        }
        deletions.erase(deletion);
    });
}

// The .torrent files hold no torrent's data, so they need not wait for
// libtorrent to release anything.
void Engine::State::DeleteTorrentFiles(std::vector<std::filesystem::path> files, DeletionMode mode, std::string names)
{
    // A recorded .torrent file is the add source as it arrived, which can be
    // relative or use forward slashes.
    for (auto& file : files)
        file = FullPath(file);
    auto folder = files.front().parent_path();
    payload.Run([files = std::move(files), mode]
    {
        Erase(files, mode, {});
    }, [this, names = std::move(names), folder = std::move(folder)](StorageOutcome outcome)
    {
        if (outcome.failure)
        {
            ReportDeletion(outcome, names, folder);
        }
    });
}

// The toast offers the folder, where the person deletes the rest in File
// Explorer.
void Engine::State::ReportDeletion(StorageOutcome const& outcome, std::string names,
    std::filesystem::path const& folder)
{
    // A busy store refused the deletion before it began, so it has no detail.
    std::string code = outcome.failure == StorageFailure::Overloaded ? ToString(ErrorCode::Overloaded) : "";
    log.Write("delete", "", code.empty() ? outcome.detail : code);
    Notify(ToastKind::DeleteFailed, std::move(names), outcome.detail, {}, code, Utf8(folder.wstring()));
}

void Engine::State::On(lt::torrent_deleted_alert const& alert)
{
    for (auto& deletion : deletions)
    {
        std::erase(deletion.waiting, alert.handle);
    }
    ContinueDeletion();
}

void Engine::State::On(lt::torrent_removed_alert const& alert)
{
    for (auto& deletion : deletions)
    {
        // Ordinary Remove keeps the files, but holds their ownership until
        // libtorrent's disk-stop callback has finished all outstanding writes.
        if (!deletion.mode)
            std::erase(deletion.waiting, alert.handle);
    }
    ContinueDeletion();
}

void Engine::State::On(lt::torrent_delete_failed_alert const& alert)
{
    for (auto& deletion : deletions)
    {
        if (std::erase(deletion.waiting, alert.handle))
        {
            log.Write("delete", "", "partfile_delete_failed");
            Notify(ToastKind::DeleteFailed, deletion.names, alert.error.message(), {}, {},
                Utf8(deletion.roots.front().wstring()));
        }
    }
    ContinueDeletion();
}

// After libtorrent dropped alerts, a deletion that waited for an alert waits
// instead for its torrents' handles to become invalid.
void Engine::State::RecoverDeletions()
{
    for (auto& deletion : deletions)
    {
        if (!deletion.waiting.empty())
        {
            deletion.phase = DeletionPhase::Unknown;
        }
    }
    ContinueDeletion();
}
}
