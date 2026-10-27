#include "Engine/State.h"
#include <Windows.h>
#include <Shellapi.h>
#include <ShlObj.h>
#include <wrl.h>
#include <algorithm>
#include <system_error>

namespace tt
{
// Removes the torrents from the list once the saved list no longer holds
// them. With a mode it also deletes their files. CanRemove and NamesReady
// decide only when each handle goes to libtorrent: now, or later from
// RemoveDeferred. With a torrentFiles mode it deletes the .torrent files they
// were added from once the list no longer holds them.
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
    auto selected = [&ids](std::string const& id) { return Contains(ids, id); };
    auto document = Saved();
    // The torrents that leave libtorrent later keep their deletion as a task
    // from this save on. Without every torrent's names, the files they keep
    // are unknown, so their deletion is a task only once it starts.
    if (mode && !later.empty() && namesReady)
        document.tasks.push_back({DeferredDeletion(later, *mode)});
    std::erase_if(document.torrents, [&selected](auto const& entry) { return selected(entry.first); });
    std::erase_if(document.queueOrder, selected);
    auto order = document.queueOrder;
    changes.Commit([document = std::move(document)] { return document.ToJson(); },
        [this, ids, ready, order = std::move(order), deletion, kept, completion, mode, torrentFiles,
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
        Unask(ids);
        for (auto const& id : ids)
        {
            Extract(id, mode);
        }
        RemoveHandles(ready, deletion);
        if (!sources.empty())
            DeleteTorrentFiles(sources, *torrentFiles, sourceNames);
        ContinueMoves();
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
    deleting.ids = ids;
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
        auto const& torrent = Working(id);
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
        auto const& torrent = removed.at(id).torrent;
        if (!torrent.restore)
        {
            if (deletion->mode)
            {
                session->remove_torrent(torrent.handle, lt::session::delete_partfile);
            }
            else
            {
                session->remove_torrent(torrent.handle);
            }
            deletion->waiting.push_back(torrent.handle);
        }
        removed.erase(id);
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
    }, [this](StorageOutcome cleanup)
    {
        if (cleanup.failure)
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

// Runs once the removal's document commit has succeeded. The torrent is
// paused, and its edit and detail reading end now, so nothing that answers the
// window waits for its files.
void Engine::State::Extract(std::string const& id, std::optional<DeletionMode> mode)
{
    auto& torrent = torrents.at(id);
    if (torrent.priorityCompletion)
    {
        std::exchange(torrent.priorityCompletion, nullptr)({ErrorCode::TorrentRemoved});
    }
    if (reading && reading->torrentId == id)
    {
        FailReading(Failure(ErrorCode::TorrentRemoved));
    }
    torrent.facts.intent = Intent::Paused;
    // A release or reload that uses its files still ends first.
    Answer(torrent, FailureAnswer::Remove);
    handles.erase(torrent.handle);
    seedQueries.erase(torrent.handle);
    removed.emplace(id, Removal{std::move(torrents.extract(id).mapped()), mode});
}

bool Engine::State::Removing(lt::info_hash_t const& hashes) const
{
    // A conflicting restore was never loaded, so libtorrent does not hold it.
    auto values = Hashes(hashes);
    return std::any_of(removed.begin(), removed.end(), [&values](auto const& entry)
        { return !entry.second.torrent.restore && Overlaps(values, entry.second.torrent.Hashes()); });
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
        for (auto const& [id, removal] : removed)
            if (removal.mode == mode && CanRemove(removal.torrent, mode))
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
            // Another removed handle can still flush writes to a shared file,
            // and the files of a torrent whose move was cut are deleted once
            // they are back in its save folder.
            bool held = std::any_of(deletions.begin(), deletions.end(), [&deletion](auto const& other)
            {
                return (other.phase == DeletionPhase::Saving || !other.waiting.empty()) &&
                    std::any_of(deletion->files.begin(), deletion->files.end(), [&other](auto const& file)
                    {
                        return std::binary_search(other.holds.begin(), other.holds.end(), file, PathBefore);
                    });
            }) || std::any_of(returning.begin(), returning.end(), [&deletion](Task::Return const& running)
                { return Contains(deletion->ids, running.torrentId); });
            // Exit leaves the deletion to the saved task.
            if (!held && !IsShuttingDown())
            {
                Delete(deletion);
            }
        }
    }
}

namespace
{
// Cancels a Recycle Bin operation before its next file once `stop` is
// requested. Windows then cancels every file still pending and leaves it
// where it was, so each file is either in the Recycle Bin or untouched.
class StopBeforeNext final : public Microsoft::WRL::RuntimeClass<
    Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>, IFileOperationProgressSink>
{
public:
    explicit StopBeforeNext(std::stop_token stop) : stop_(std::move(stop)) {}

    HRESULT STDMETHODCALLTYPE PreDeleteItem(DWORD, IShellItem*) override
    {
        return stop_.stop_requested() ? HRESULT_FROM_WIN32(ERROR_CANCELLED) : S_OK;
    }
    HRESULT STDMETHODCALLTYPE StartOperations() override { return S_OK; }
    HRESULT STDMETHODCALLTYPE FinishOperations(HRESULT) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE PreRenameItem(DWORD, IShellItem*, LPCWSTR) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE PostRenameItem(DWORD, IShellItem*, LPCWSTR, HRESULT, IShellItem*) override
    {
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE PreMoveItem(DWORD, IShellItem*, IShellItem*, LPCWSTR) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE PostMoveItem(DWORD, IShellItem*, IShellItem*, LPCWSTR, HRESULT, IShellItem*) override
    {
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE PreCopyItem(DWORD, IShellItem*, IShellItem*, LPCWSTR) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE PostCopyItem(DWORD, IShellItem*, IShellItem*, LPCWSTR, HRESULT, IShellItem*) override
    {
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE PostDeleteItem(DWORD, IShellItem*, HRESULT, IShellItem*) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE PreNewItem(DWORD, IShellItem*, LPCWSTR) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE PostNewItem(DWORD, IShellItem*, LPCWSTR, LPCWSTR, DWORD, HRESULT, IShellItem*) override
    {
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE UpdateProgress(UINT, UINT) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE ResetTimer() override { return S_OK; }
    HRESULT STDMETHODCALLTYPE PauseTimer() override { return S_OK; }
    HRESULT STDMETHODCALLTYPE ResumeTimer() override { return S_OK; }

private:
    std::stop_token stop_;
};

// Sends the files to the Recycle Bin and throws the first failure. A file that
// is already gone counts as deleted. Once `stop` is requested, it sends no
// further file and leaves the rest without a failure.
void Recycle(std::vector<std::filesystem::path> const& files, std::stop_token stop)
{
    auto initialized = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    IFileOperation* operation = nullptr;
    auto result = FAILED(initialized) ? initialized : CoCreateInstance(CLSID_FileOperation, nullptr,
        CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&operation));
    if (SUCCEEDED(result))
        result = operation->SetOperationFlags(FOFX_RECYCLEONDELETE | FOFX_EARLYFAILURE |
            FOF_NOERRORUI | FOF_SILENT | FOF_NOCONFIRMATION | FOF_NO_CONNECTED_ELEMENTS);
    auto sink = Microsoft::WRL::Make<StopBeforeNext>(stop);
    DWORD cookie = 0;
    if (SUCCEEDED(result))
        result = sink ? operation->Advise(sink.Get(), &cookie) : E_OUTOFMEMORY;
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
    if (stop.stop_requested())
        return;
    if (FAILED(result))
        throw std::system_error(
            HRESULT_FACILITY(result) == FACILITY_WIN32 ? HRESULT_CODE(result) : result,
            std::system_category(), "HRESULT " + std::to_string(result));
    if (aborted)
        throw std::runtime_error("recycle_failed");
}
}

void Engine::State::Erase(std::vector<std::filesystem::path> const& files, DeletionMode mode,
    std::vector<std::filesystem::path> const& roots, std::stop_token stop)
{
    std::exception_ptr failure;
    try
    {
        if (mode == DeletionMode::Recycle)
            Recycle(files, stop);
        else
            Payload::Delete(files, stop);
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
    payload.Run([deleting, stop = exitStop.get_token()]
    {
        Erase(deleting.files, *deleting.mode, deleting.roots, stop);
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
