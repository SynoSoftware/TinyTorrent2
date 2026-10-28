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
// them. With a mode it also deletes their files. CanRemove and NamesReadyFor
// decide only when each handle goes to libtorrent: now, or later from
// RemoveDeferred. With a torrentFiles mode it deletes the .torrent files they
// were added from once the list no longer holds them.
void Engine::State::Remove(std::vector<std::string> const& ids,
    std::function<void(Outcome, std::optional<std::size_t> kept)> completion,
    std::optional<DeletionMode> mode, std::optional<DeletionMode> torrentFiles)
{
    auto namesReady = !mode || NamesReadyFor(ids);
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
    // Each .torrent file is a deletion of its own, bounded by its own folder,
    // so no folder that it leaves empty goes. A recorded file is the add
    // source as it arrived, which can be relative or use forward slashes.
    std::vector<Task::Deletion> sources;
    if (torrentFiles)
    {
        for (auto const& id : ids)
        {
            auto const& torrent = torrents.at(id);
            if (torrent.facts.torrentFile.empty())
                continue;
            auto file = FullPath(Wide(torrent.facts.torrentFile));
            sources.push_back({{id}, *torrentFiles, {file}, {file.parent_path()}, torrent.Name()});
        }
    }
    // The give-backs of the torrents' moves, which run once each torrent's
    // path work has ended.
    std::vector<Task::Return> givesBack;
    for (auto const& id : ids)
    {
        if (auto returned = GiveBackOf(torrents.at(id)))
            givesBack.push_back(std::move(*returned));
    }
    auto selected = [&ids](std::string const& id) { return Contains(ids, id); };
    auto document = Saved();
    // The removal's own commit saves its file work, and no earlier save holds
    // it: the deletion of the torrents that leave libtorrent now, which
    // Saved() holds, and the give-backs and the deletion of the torrents that
    // leave later, whose owners take them only once this commit succeeds.
    for (auto const& returned : givesBack)
        document.tasks.push_back({returned});
    for (auto const& source : sources)
        document.tasks.push_back({source});
    if (mode && !later.empty())
        document.tasks.push_back({DeferredDeletion(later, *mode)});
    std::erase_if(document.torrents, [&selected](auto const& entry) { return selected(entry.first); });
    std::erase_if(document.queueOrder, selected);
    auto order = document.queueOrder;
    changes.Commit([document = std::move(document)] { return document.ToJson(); },
        [this, ids, ready, order = std::move(order), deletion, kept, completion, mode,
            givesBack = std::move(givesBack), sources = std::move(sources)](Outcome outcome)
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
        for (auto const& returned : givesBack)
            AddGiveBack(returned);
        for (auto const& id : ids)
        {
            Extract(id, mode);
        }
        RemoveHandles(ready, deletion);
        for (auto const& source : sources)
            AddDeletion(source);
        ContinueDeletion();
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
    std::vector<std::filesystem::path> files;
    if (mode)
    {
        deleting.holds = std::move(scope.holds);
        for (auto const& file : scope.files)
        {
            if (!std::binary_search(scope.kept.begin(), scope.kept.end(), file, PathBefore))
                files.push_back(file);
        }
    }
    // libtorrent removes each torrent on the root of its save folder, where
    // its files lie too.
    std::vector<FileWork::Use> uses;
    for (auto const& id : ids)
    {
        auto const& torrent = Working(id);
        uses.push_back({torrent.facts.savePath, id});
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
    Split(deleting, std::move(files), std::move(uses));
    return deletions.insert(deletions.end(), std::move(deleting));
}

void Engine::State::Split(Deletion& deletion, std::vector<std::filesystem::path> files,
    std::vector<FileWork::Use> uses)
{
    std::map<std::string, std::vector<FileWork::Use>> used;
    for (auto& use : uses)
        used[RootOf(use.folder)].push_back(std::move(use));
    std::map<std::string, std::vector<std::filesystem::path>> held;
    for (auto& file : files)
        held[RootOf(Utf8(file.wstring()))].push_back(std::move(file));
    for (auto const& [root, part] : held)
    {
        // Every torrent's files lie in its own folder. Files elsewhere, which
        // only a damaged task holds, count for each torrent of the deletion.
        if (used.contains(root))
            continue;
        auto folder = Utf8(part.front().parent_path().wstring());
        for (auto const& id : deletion.ids)
            used[root].push_back({folder, id});
        if (deletion.ids.empty())
            used[root].push_back({folder, {}});
    }
    for (auto& [root, rootUses] : used)
    {
        auto& part = deletion.parts[root];
        part.files = std::move(held[root]);
        std::sort(part.files.begin(), part.files.end(), PathBefore);
        part.job = fileWork.Start(WorkKind::Deletion, std::move(rootUses));
    }
}

void Engine::State::AddDeletion(Task::Deletion const& task)
{
    Deletion deletion;
    deletion.ids = task.ids;
    deletion.mode = task.mode;
    deletion.holds = task.files;
    std::sort(deletion.holds.begin(), deletion.holds.end(), PathBefore);
    deletion.roots = task.roots;
    deletion.names = task.names;
    deletion.cleans = task.cleans;
    deletion.phase = DeletionPhase::Waiting;
    std::vector<FileWork::Use> uses;
    for (std::size_t index = 0; index < std::min(task.ids.size(), task.roots.size()); ++index)
        uses.push_back({Utf8(task.roots[index].wstring()), task.ids[index]});
    Split(deletion, task.files, std::move(uses));
    deletions.push_back(std::move(deletion));
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
            // libtorrent's disk work runs on the root of the part that uses
            // the torrent's folder until it lets go. The part is found by its
            // use, because a lookup since the split can name another root.
            auto part = std::find_if(deletion->parts.begin(), deletion->parts.end(), [&torrent, &id](auto const& entry)
            {
                auto const& uses = entry.second.job->uses;
                return std::any_of(uses.begin(), uses.end(), [&torrent, &id](FileWork::Use const& use)
                    { return use.torrentId == id && SameFolder(use.folder, torrent.facts.savePath); });
            });
            if (part == deletion->parts.end())
                throw std::runtime_error("A removed torrent's folder has no part in its deletion");
            part->second.waiting.push_back(torrent.handle);
            part->second.job->Run();
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
// move or rename, which it cannot stop halfway, and once the reads of its
// check have returned, so StopStalledJobs still times a check whose reads
// hang. Deleting its files also waits until no addition runs on its roots,
// because FileScope cannot see the files an addition shares.
bool Engine::State::CanRemove(Torrent const& torrent, std::optional<DeletionMode> mode) const
{
    auto const& marker = torrent.facts.marker;
    bool adding = Adding(RootOf(torrent.facts.savePath)) || (marker && Adding(RootOf(marker->destination)));
    return !torrent.FilesBusy() && torrent.checkReads == CheckReads::None && (!mode || !adding);
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
    auto job = fileWork.Start(WorkKind::Removal, {{torrent.facts.savePath, id}});
    job->Run();
    removed.emplace(id, Removal{std::move(torrents.extract(id).mapped()), mode, std::move(job)});
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
    for (auto mode : {std::optional(DeletionMode::Recycle), std::optional(DeletionMode::Permanent),
        std::optional<DeletionMode>()})
    {
        // Only a deletion needs the names of the torrents whose files can lie
        // in its folders, to find the files they keep.
        std::vector<std::string> ready;
        for (auto const& [id, removal] : removed)
        {
            if (removal.mode == mode && CanRemove(removal.torrent, mode) && (!mode || NamesReadyFor({id})))
                ready.push_back(id);
        }
        if (!ready.empty())
            RemoveHandles(ready, PrepareDeletion(ready, mode, mode ? FileScope(ready) : Scope{}));
    }
}

bool Engine::State::Deletion::Part::Release(std::function<bool(lt::torrent_handle const&)> const& released)
{
    if (std::erase_if(waiting, released) == 0)
        return false;
    if (waiting.empty())
        job->Queue();
    return true;
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
            for (auto& [root, part] : deletion.parts)
                part.Release([](auto const& handle) { return !handle.is_valid(); });
        }
    }
    for (auto deletion = deletions.begin(); deletion != deletions.end();)
    {
        // The removal's own commit holds a deletion that it saves.
        if (deletion->phase == DeletionPhase::Saving)
        {
            ++deletion;
            continue;
        }
        for (auto part = deletion->parts.begin(); part != deletion->parts.end();)
        {
            if (part->second.job->Phase() != JobPhase::Queued)
            {
                ++part;
                continue;
            }
            // A part without files, such as a removal's that keeps them, has
            // nothing to delete once libtorrent has released them.
            if (part->second.files.empty())
            {
                part = deletion->parts.erase(part);
                continue;
            }
            // Exit leaves the deletion to the saved task.
            if (!IsShuttingDown() && !Held(*deletion, part->first))
                Delete(deletion, part->first);
            ++part;
        }
        deletion = deletion->parts.empty() ? deletions.erase(deletion) : std::next(deletion);
    }
}

bool Engine::State::Held(Deletion const& deletion, std::string const& root) const
{
    auto const& part = deletion.parts.at(root);
    auto const& files = part.files;
    auto holds = [&files](auto const& held)
    {
        return std::any_of(files.begin(), files.end(), [&held](auto const& file)
            { return std::binary_search(held.begin(), held.end(), file, PathBefore); });
    };
    // The roots that a part's folders are on now, which a lookup since the
    // parts were split can have changed.
    auto rootsOf = [this](Deletion::Part const& of)
    {
        std::set<std::string> roots;
        for (auto const& use : of.job->uses)
            roots.insert(RootOf(use.folder));
        return roots;
    };
    auto mine = rootsOf(part);
    auto sameRoot = [&rootsOf, &mine](Deletion::Part const& other)
    {
        auto roots = rootsOf(other);
        return std::any_of(roots.begin(), roots.end(), [&mine](auto const& root) { return mine.contains(root); });
    };
    // Only a handle on the same root can write these files.
    return std::any_of(deletions.begin(), deletions.end(), [&holds, &sameRoot](Deletion const& other)
    {
        auto writes = [&other, &sameRoot](auto const& entry)
        {
            return (other.phase == DeletionPhase::Saving || !entry.second.waiting.empty()) && sameRoot(entry.second);
        };
        return holds(other.holds) && std::any_of(other.parts.begin(), other.parts.end(), writes);
    }) || std::any_of(returning.begin(), returning.end(), [&files](Returning const& running)
    {
        return std::any_of(running.task.files.begin(), running.task.files.end(), [&files](auto const& file)
            { return std::binary_search(files.begin(), files.end(), file.source, PathBefore); });
    });
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

void Engine::State::Delete(std::list<Deletion>::iterator deletion, std::string const& root)
{
    auto& part = deletion->parts.at(root);
    part.job->Run();
    // A deletion of many files takes long, so it runs on a worker of its own
    // and the short jobs of its root, such as an addition's names, never
    // wait behind it.
    part.job->worker = std::make_shared<Store>(wake);
    auto folders = part.job->Folders();
    part.job->worker->Run([files = part.files, folders, mode = *deletion->mode,
        roots = deletion->roots, stop = exitStop.get_token()]
    {
        return Reach(folders, [&] { Erase(files, mode, roots, stop); });
    }, [this, deletion, root, folder = folders.front()](std::expected<std::optional<std::string>, StorageOutcome> erased)
    {
        // Exit stopped the deletion, and its record continues it at the next
        // start. Files that are already gone count as deleted then.
        if (IsShuttingDown())
            return;
        auto part = deletion->parts.find(root);
        // The part keeps its files and waits for its root's probe to open
        // the root, and the location notice counts its torrents meanwhile.
        if (erased && *erased)
        {
            log.Write("delete", "", "unreachable");
            part->second.job->Await(**erased);
            return;
        }
        // A moved torrent's old files are also at its new folder, so their
        // failure only leaves copies behind.
        if (!erased && deletion->cleans)
            log.Write("move", deletion->ids.empty() ? std::string() : deletion->ids.front(), erased.error().detail);
        else if (!erased)
            ReportDeletion(erased.error(), deletion->names, Wide(folder));
        deletion->parts.erase(part);
        if (deletion->parts.empty())
            deletions.erase(deletion);
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
        for (auto& [root, part] : deletion.parts)
            part.Release([&alert](auto const& handle) { return handle == alert.handle; });
    }
    ContinueDeletion();
}

void Engine::State::On(lt::torrent_removed_alert const& alert)
{
    for (auto& deletion : deletions)
    {
        // Ordinary Remove keeps the files, but holds their ownership until
        // libtorrent's disk-stop callback has finished all outstanding writes.
        if (deletion.mode)
            continue;
        for (auto& [root, part] : deletion.parts)
            part.Release([&alert](auto const& handle) { return handle == alert.handle; });
    }
    ContinueDeletion();
}

void Engine::State::On(lt::torrent_delete_failed_alert const& alert)
{
    for (auto& deletion : deletions)
    {
        for (auto& [root, part] : deletion.parts)
        {
            // libtorrent also posts it without an error for a torrent that
            // has no storage, such as a magnet without metadata, which has no
            // files.
            if (part.Release([&alert](auto const& handle) { return handle == alert.handle; }) && alert.error)
            {
                log.Write("delete", "", "partfile_delete_failed");
                Notify(ToastKind::DeleteFailed, deletion.names, alert.error.message(), {}, {},
                    part.job->uses.front().folder);
            }
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
        if (std::any_of(deletion.parts.begin(), deletion.parts.end(),
            [](auto const& entry) { return !entry.second.waiting.empty(); }))
        {
            deletion.phase = DeletionPhase::Unknown;
        }
    }
    ContinueDeletion();
}
}
