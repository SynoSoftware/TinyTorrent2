#include "Engine/State.h"
#include <Windows.h>
#include <libtorrent/aux_/path.hpp>
#include <algorithm>
#include <set>
#include <system_error>

namespace tt
{
namespace
{
Json SavedPaths(std::vector<std::filesystem::path> const& paths)
{
    Json list = Json::array();
    for (auto const& path : paths)
        list.push_back(Utf8(path.wstring()));
    return list;
}

// The strings of a saved list, or nothing when it is not a list of strings.
std::optional<std::vector<std::string>> SavedStrings(Json const& saved, char const* key)
{
    auto found = saved.find(key);
    if (found == saved.end() || !found->is_array())
        return std::nullopt;
    std::vector<std::string> values;
    for (auto const& value : *found)
    {
        if (!value.is_string())
            return std::nullopt;
        values.push_back(value.get<std::string>());
    }
    return values;
}
}

std::filesystem::path Engine::State::ShareRoot(std::filesystem::path const& folder)
{
    auto root = folder.root_path();
    // A UNC root names only the server; the share is part of the root.
    if (folder.root_name().wstring().starts_with(LR"(\\)") && folder.has_relative_path())
        root /= *folder.relative_path().begin();
    return root;
}

std::optional<std::filesystem::path> Engine::State::ExistingFolder(std::filesystem::path folder)
{
    auto root = ShareRoot(folder);
    while (true)
    {
        // Opening the folder follows a link or junction to its target, which
        // reading its attributes does not. It asks for no access, so a folder
        // that refuses access still answers.
        auto opened = CreateFileW(folder.c_str(), 0, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, nullptr);
        if (opened != INVALID_HANDLE_VALUE)
        {
            CloseHandle(opened);
            return folder;
        }
        auto error = GetLastError();
        if (error == ERROR_ACCESS_DENIED)
            return folder;
        auto parent = folder.parent_path();
        if ((error != ERROR_FILE_NOT_FOUND && error != ERROR_PATH_NOT_FOUND) || folder == root || parent == folder)
            return std::nullopt;
        folder = std::move(parent);
    }
}

// Whether the folder, or the nearest of its ancestors that exists, answers.
// libtorrent's rule decides, so the engine and libtorrent agree on a link
// whose target does not answer.
bool Engine::State::Reachable(std::filesystem::path const& folder)
{
    auto error = boost::system::errc::make_error_code(boost::system::errc::no_such_file_or_directory);
    lt::check_storage_root(error, Utf8(folder.wstring()));
    return error != Unreachable();
}

lt::error_code Engine::State::Unreachable()
{
    return {ERROR_DEV_NOT_EXIST, lt::system_category()};
}

void Engine::State::CheckReachable(std::filesystem::path const& folder)
{
    if (!Reachable(folder))
        throw lt::system_error(Unreachable(), Utf8(folder.wstring()));
}

std::set<std::string> Engine::State::Holders(std::vector<Task> const& tasks)
{
    std::set<std::string> ids;
    for (auto const& task : tasks)
    {
        if (auto move = std::get_if<Task::Move>(&task.work))
            ids.insert(move->ids.begin(), move->ids.end());
        else if (auto check = std::get_if<Task::Check>(&task.work))
            ids.insert(check->torrentId);
    }
    return ids;
}

void Engine::State::AddMove(std::vector<Task>& tasks, std::size_t first, std::string const& id,
    std::string const& destination)
{
    for (auto task = tasks.begin() + first; task != tasks.end(); ++task)
    {
        auto move = std::get_if<Task::Move>(&task->work);
        if (move && SameFolder(move->destination, destination))
        {
            move->ids.push_back(id);
            return;
        }
    }
    tasks.push_back({Task::Move{{id}, destination}});
}

Json Engine::State::Task::ToJson() const
{
    if (auto move = std::get_if<Move>(&work))
        return {{"kind", "move"}, {"ids", move->ids}, {"destination", move->destination}};
    if (auto check = std::get_if<Check>(&work))
        return {{"kind", "check"}, {"torrent_id", check->torrentId}};
    if (auto returned = std::get_if<Return>(&work))
    {
        Json files = Json::array();
        for (auto const& file : returned->files)
            files.push_back({{"source", Utf8(file.source.wstring())}, {"destination", Utf8(file.destination.wstring())}});
        return {{"kind", "return"}, {"torrent_id", returned->torrentId}, {"name", returned->name},
            {"root", Utf8(returned->root.wstring())}, {"files", std::move(files)}};
    }
    auto const& deletion = std::get<Deletion>(work);
    return {{"kind", "delete"}, {"ids", deletion.ids}, {"mode", static_cast<int>(deletion.mode)},
        {"files", SavedPaths(deletion.files)}, {"roots", SavedPaths(deletion.roots)}, {"names", deletion.names},
        {"cleans", deletion.cleans}};
}

// A task saved incorrectly is dropped, so one damaged task does not cost the
// others.
std::optional<Engine::State::Task> Engine::State::Task::Read(Json const& saved)
{
    if (!saved.is_object())
        return std::nullopt;
    auto kind = ReadSaved(saved, "kind", std::string());
    if (kind == "move")
    {
        auto ids = SavedStrings(saved, "ids");
        auto destination = ReadSaved(saved, "destination", std::string());
        if (!ids || ids->empty() || !IsAbsolute(destination))
            return std::nullopt;
        return Task{Move{std::move(*ids), std::move(destination)}};
    }
    if (kind == "check")
    {
        auto id = ReadSaved(saved, "torrent_id", std::string());
        if (id.empty())
            return std::nullopt;
        return Task{Check{std::move(id)}};
    }
    if (kind == "return")
    {
        auto root = ReadSaved(saved, "root", std::string());
        auto files = saved.find("files");
        if (!IsAbsolute(root) || files == saved.end() || !files->is_array())
            return std::nullopt;
        Return returned{ReadSaved(saved, "torrent_id", std::string()), ReadSaved(saved, "name", std::string()),
            Wide(root)};
        for (auto const& file : *files)
        {
            auto source = ReadSaved(file, "source", std::string());
            auto destination = ReadSaved(file, "destination", std::string());
            if (!IsAbsolute(source) || !IsAbsolute(destination))
                return std::nullopt;
            returned.files.push_back({Wide(source), Wide(destination)});
        }
        return Task{std::move(returned)};
    }
    if (kind != "delete")
        return std::nullopt;
    auto ids = SavedStrings(saved, "ids");
    auto files = SavedStrings(saved, "files");
    auto roots = SavedStrings(saved, "roots");
    auto mode = ReadSaved(saved, "mode", -1);
    if (!ids || !files || !roots || roots->empty() || mode < 0 || mode > static_cast<int>(DeletionMode::Permanent))
        return std::nullopt;
    Deletion deletion{std::move(*ids), static_cast<DeletionMode>(mode)};
    for (auto const& file : *files)
    {
        if (!IsAbsolute(file))
            return std::nullopt;
        deletion.files.push_back(Wide(file));
    }
    for (auto const& root : *roots)
    {
        if (!IsAbsolute(root))
            return std::nullopt;
        deletion.roots.push_back(Wide(root));
    }
    deletion.names = ReadSaved(saved, "names", std::string());
    deletion.cleans = ReadSaved(saved, "cleans", false);
    return Task{std::move(deletion)};
}

Json Engine::State::Document::SavedTasks() const
{
    Json list = Json::array();
    for (auto task : tasks)
    {
        if (auto move = std::get_if<Task::Move>(&task.work))
        {
            std::erase_if(move->ids, [this, move](std::string const& id)
            {
                auto found = torrents.find(id);
                return found == torrents.end() ||
                    (!found->second.marker && SameFolder(found->second.savePath, move->destination));
            });
            if (move->ids.empty())
                continue;
        }
        else if (auto check = std::get_if<Task::Check>(&task.work))
        {
            if (!torrents.contains(check->torrentId))
                continue;
        }
        else if (auto returned = std::get_if<Task::Return>(&task.work))
        {
            if (returned->files.empty())
                continue;
        }
        else
        {
            // A torrent whose new folder is not saved yet still needs its old
            // files.
            auto const& deletion = std::get<Task::Deletion>(task.work);
            if (deletion.files.empty() || (deletion.cleans && std::any_of(deletion.ids.begin(), deletion.ids.end(),
                [this](auto const& id) { auto found = torrents.find(id); return found != torrents.end() &&
                    found->second.marker; })))
                continue;
        }
        list.push_back(task.ToJson());
    }
    return list;
}

std::vector<Engine::State::Task> Engine::State::Tasks() const
{
    auto tasks = asked;
    auto held = Holders(asked);
    // A torrent removed during its move keeps the record of its give-back
    // until the save of its new folder ends it.
    auto givesBack = [this](std::string const& id)
    {
        return std::any_of(returning.begin(), returning.end(),
            [&id](Returning const& running) { return running.task.torrentId == id; });
    };
    for (auto const& moving : moves)
    {
        // A give-back needs the sources, so they go only once the save of the
        // new folder has ended its record.
        if (moving.cleaning && std::none_of(moving.cleaning->ids.begin(), moving.cleaning->ids.end(), givesBack))
            tasks.push_back({*moving.cleaning});
        std::vector<std::string> ids;
        for (auto id = moving.ids.begin() + std::min(moving.current, moving.ids.size()); id != moving.ids.end(); ++id)
        {
            if (held.insert(*id).second)
                ids.push_back(*id);
        }
        if (!ids.empty())
            tasks.push_back({Task::Move{std::move(ids), moving.destination}});
    }
    // The checks and the moves that wait for their turn, in the order of
    // their turns.
    std::vector<std::pair<std::uint64_t, Task>> turns;
    for (auto const& [id, torrent] : torrents)
    {
        if (torrent.checkTurn && !held.contains(id))
            turns.push_back({torrent.checkTurn->turn, {Task::Check{id}}});
    }
    for (auto& job : WaitingMoves())
    {
        std::erase_if(job.ids, [&held](auto const& id) { return held.contains(id); });
        if (!job.ids.empty())
            turns.push_back({job.turn, {Task::Move{std::move(job.ids), *job.destination}}});
    }
    std::stable_sort(turns.begin(), turns.end(), [](auto const& left, auto const& right)
        { return left.first < right.first; });
    for (auto& [turn, task] : turns)
        tasks.push_back(std::move(task));
    for (auto const& [id, torrent] : torrents)
    {
        if (torrent.needsRecheck && !torrent.checkTurn && !held.contains(id))
            tasks.push_back({Task::Check{id}});
    }
    // The give-backs of removed torrents' moves, before the deletions, which
    // wait until the files are back.
    for (auto const& running : returning)
        tasks.push_back({running.task});
    // A removal's deletion is saved first by the removal's own commit, which
    // holds the changes queue from its preparation to its end.
    for (auto const& deletion : deletions)
    {
        if (!deletion.mode)
            continue;
        Task::Deletion task{deletion.ids, *deletion.mode, {}, deletion.roots, deletion.names, deletion.cleans};
        for (auto const& [root, part] : deletion.parts)
            task.files.insert(task.files.end(), part.files.begin(), part.files.end());
        tasks.push_back({std::move(task)});
    }
    if (!removed.empty())
    {
        for (auto mode : {DeletionMode::Recycle, DeletionMode::Permanent})
        {
            std::vector<std::string> ids;
            for (auto const& [id, removal] : removed)
            {
                if (removal.mode == mode)
                    ids.push_back(id);
            }
            if (!ids.empty())
                tasks.push_back({DeferredDeletion(ids, mode)});
        }
    }
    return tasks;
}

Engine::State::Task::Deletion Engine::State::DeferredDeletion(std::vector<std::string> const& ids,
    DeletionMode mode) const
{
    auto scope = FileScope(ids);
    // Any file under the folders of a torrent whose names are not read yet
    // can be one of its files, so the deletion keeps those files.
    std::vector<std::string> unknown;
    EachWorking([&ids, &unknown](Torrent const& torrent)
    {
        if (torrent.NamesKnown() || Contains(ids, torrent.torrentId))
            return;
        unknown.push_back(torrent.facts.savePath);
        if (torrent.facts.marker)
            unknown.push_back(torrent.facts.marker->destination);
    });
    Task::Deletion deletion{ids, mode};
    for (auto const& file : scope.files)
    {
        auto folder = Utf8(file.parent_path().wstring());
        if (!std::binary_search(scope.kept.begin(), scope.kept.end(), file, PathBefore) &&
            std::none_of(unknown.begin(), unknown.end(), [&folder](auto const& used)
                { return FoldersOverlap(used, folder); }))
            deletion.files.push_back(file);
    }
    for (auto const& id : ids)
    {
        auto const& torrent = Working(id);
        deletion.roots.push_back(FullPath(Wide(torrent.facts.savePath)));
        deletion.names += (deletion.names.empty() ? "" : ", ") + torrent.Name();
    }
    return deletion;
}

void Engine::State::RecordTasks()
{
    if (startup != Startup::Ready || IsShuttingDown() || !changes.IsIdle())
        return;
    auto tasks = Tasks();
    if (tasks.empty())
        tasksContinued = false;
    if (tasks == recordedTasks)
        return;
    changes.Queue([this]
    {
        auto document = Saved();
        changes.Commit([document] { return document.ToJson(); }, [this, tasks = document.tasks](Outcome outcome)
        {
            if (outcome.error)
            {
                recordedTasks.reset();
                log.Write("tasks", "", outcome.detail);
            }
            else
                recordedTasks = tasks;
        });
    });
}

bool Engine::State::RecordCopies()
{
    std::vector<std::pair<std::string, Facts::Marker::Copy>> proofs;
    for (auto const& moving : moves)
    {
        auto const& progress = moving.job->progress;
        if (!progress || moving.current >= moving.ids.size())
            continue;
        auto proof = progress->Proven();
        auto const& id = moving.ids[moving.current];
        auto torrent = torrents.find(id);
        if (!proof || torrent == torrents.end() || !torrent->second.facts.marker)
            continue;
        Facts::Marker::Copy copy{Utf8(proof->destination.wstring()), proof->bytes};
        auto const& saved = torrent->second.facts.marker->copy;
        if (!saved || saved->path != copy.path || saved->bytes != copy.bytes)
            proofs.push_back({id, std::move(copy)});
    }
    if (proofs.empty() || !changes.IsIdle())
        return false;
    return changes.Queue([this, proofs]
    {
        std::vector<std::string> ids;
        for (auto const& [id, copy] : proofs)
        {
            if (torrents.contains(id))
                ids.push_back(id);
        }
        CommitFacts(ids, [proofs](std::string const& id, Facts& facts)
        {
            for (auto const& [proven, copy] : proofs)
            {
                if (proven == id && facts.marker)
                    facts.marker->copy = copy;
            }
        }, [this](Outcome outcome, auto const&)
        {
            if (outcome.error)
            {
                log.Write("move", "", outcome.detail);
                FailFinalSave(outcome.detail);
            }
        });
    });
}

void Engine::State::Unfinished(std::vector<Task> tasks, bool continued)
{
    // Giving files back is what either answer would do, so it continues
    // without asking: the give-backs of removed torrents' moves, and the
    // moves that the person abandoned.
    std::vector<Task> returns;
    for (auto task = tasks.begin(); task != tasks.end();)
    {
        bool gone = false;
        if (auto move = std::get_if<Task::Move>(&task->work))
        {
            std::erase_if(move->ids, [this](auto const& id) { return !torrents.contains(id); });
            gone = move->ids.empty();
        }
        else if (auto check = std::get_if<Task::Check>(&task->work))
            gone = !torrents.contains(check->torrentId);
        else if (std::holds_alternative<Task::Return>(task->work))
        {
            returns.push_back(std::move(*task));
            gone = true;
        }
        task = gone ? tasks.erase(task) : task + 1;
    }
    ContinueTasks(returns);
    // A marker that no saved task holds is a move that a crash cut before
    // its task was saved.
    auto held = Holders(tasks);
    auto first = tasks.size();
    for (auto& [id, torrent] : torrents)
    {
        auto const& marker = torrent.facts.marker;
        if (!marker || marker->failed || held.contains(id))
            continue;
        if (marker->stage != MoveStage::Returning)
            AddMove(tasks, first, id, marker->destination);
        else
            JoinAbandonments(torrent);
    }
    if (tasks.empty())
        return;
    log.Write("startup", "", "unfinished_tasks");
    if (settings.unfinished == TaskAnswer::Continue && !continued)
    {
        tasksContinued = true;
        ContinueTasks(tasks);
        return;
    }
    // The asked tasks hold their torrents. With the abandon setting they hold
    // them until the abandonment is saved, as for the person's answer, and
    // stay for the person when the save queue refuses.
    Ask(std::move(tasks));
    if (settings.unfinished == TaskAnswer::Abandon)
    {
        if (!changes.Queue([this] { AbandonTasks(std::exchange(asked, {}), [](Outcome) {}); }))
            log.Write("startup", "", "storage_overloaded");
    }
}

void Engine::State::Ask(std::vector<Task> tasks)
{
    std::vector<std::string> gone;
    for (auto const& id : Holders(tasks))
    {
        if (!torrents.contains(id))
            gone.push_back(id);
    }
    DropTorrents(tasks, gone);
    // The rows show the waiting work without a toast for each torrent.
    for (auto const& task : tasks)
    {
        if (auto move = std::get_if<Task::Move>(&task.work))
        {
            for (auto const& id : move->ids)
            {
                auto& torrent = torrents.at(id);
                torrent.unfinished = TaskKind::Move;
                if (auto problem = torrent.Error())
                    torrent.notifiedError = problem;
            }
        }
        else if (auto check = std::get_if<Task::Check>(&task.work))
            torrents.at(check->torrentId).unfinished = TaskKind::Check;
    }
    asked.insert(asked.end(), std::make_move_iterator(tasks.begin()), std::make_move_iterator(tasks.end()));
}

void Engine::State::Answer(TaskAnswer answer, std::function<void(Outcome)> completion)
{
    if (!changes.Queue([this, answer, completion]
    {
        auto tasks = std::exchange(asked, {});
        auto ids = Holders(tasks);
        auto done = [this, ids, completion](Outcome outcome)
        {
            for (auto const& id : ids)
            {
                if (auto found = torrents.find(id); found != torrents.end())
                    found->second.ApplyIntent();
            }
            RequestStatus();
            completion(std::move(outcome));
        };
        if (answer == TaskAnswer::Abandon)
        {
            AbandonTasks(tasks, done);
            return;
        }
        for (auto const& id : ids)
        {
            if (auto found = torrents.find(id); found != torrents.end())
                found->second.unfinished.reset();
        }
        ContinueTasks(tasks);
        changes.Commit([document = Saved()] { return document.ToJson(); }, done);
    }))
    {
        completion({ErrorCode::Overloaded});
    }
}

void Engine::State::DropTorrents(std::vector<Task>& tasks, std::vector<std::string> const& ids,
    std::optional<TaskKind> kind)
{
    std::erase_if(tasks, [&ids, kind](Task& task)
    {
        if (auto move = std::get_if<Task::Move>(&task.work); move && kind != TaskKind::Check)
        {
            std::erase_if(move->ids, [&ids](auto const& id) { return Contains(ids, id); });
            return move->ids.empty();
        }
        auto check = std::get_if<Task::Check>(&task.work);
        return check && kind != TaskKind::Move && Contains(ids, check->torrentId);
    });
}

void Engine::State::Unask(std::vector<std::string> const& ids, std::optional<TaskKind> kind)
{
    DropTorrents(asked, ids, kind);
    for (auto const& id : ids)
    {
        auto found = torrents.find(id);
        if (found != torrents.end() && (!kind || found->second.unfinished == kind))
            found->second.unfinished.reset();
    }
}

void Engine::State::ContinueTasks(std::vector<Task> const& tasks)
{
    for (auto const& task : tasks)
    {
        if (auto move = std::get_if<Task::Move>(&task.work))
        {
            auto turn = ++turns;
            for (auto const& id : move->ids)
            {
                auto found = torrents.find(id);
                if (found != torrents.end() && found->second.pathWork != PathWork::Move)
                    found->second.waitingMove = WaitingMove{move->destination, turn};
            }
        }
        else if (auto check = std::get_if<Task::Check>(&task.work))
        {
            if (auto found = torrents.find(check->torrentId); found != torrents.end())
                found->second.needsRecheck = true;
        }
        else if (auto returned = std::get_if<Task::Return>(&task.work))
            AddGiveBack(*returned);
        else
            AddDeletion(std::get<Task::Deletion>(task.work));
    }
    ContinueDeletion();
}

void Engine::State::AbandonTasks(std::vector<Task> const& tasks, std::function<void(Outcome)> done)
{
    // Moves to the final folder that had not started, which would otherwise
    // start again once the downloads finish.
    std::vector<std::string> unplanned;
    std::vector<std::string> stopped;
    // Moves whose files go back from now on, also after an end that cuts
    // the give-back off.
    std::vector<std::string> returning;
    // Staged moves, which changed no file, so the answer's save clears their
    // markers.
    std::vector<std::string> staged;
    // Moves with a marker, whose files go back once the answer is saved.
    std::vector<std::string> abandoned;
    for (auto const& task : tasks)
    {
        if (auto move = std::get_if<Task::Move>(&task.work))
        {
            for (auto const& id : move->ids)
            {
                auto found = torrents.find(id);
                if (found == torrents.end())
                    continue;
                auto& torrent = found->second;
                // The marker of a Move that the person started meanwhile is
                // that move's own.
                if (torrent.facts.marker && torrent.facts.marker->stage == MoveStage::Staged)
                {
                    if (torrent.pathWork != PathWork::Move)
                        staged.push_back(id);
                }
                else if (torrent.facts.marker)
                {
                    abandoned.push_back(id);
                    if (torrent.facts.marker->stage == MoveStage::Placing)
                        returning.push_back(id);
                }
                else if (!torrent.facts.finalFolder.empty() &&
                    SameFolder(torrent.facts.finalFolder, move->destination))
                    unplanned.push_back(id);
            }
        }
        else if (auto check = std::get_if<Task::Check>(&task.work))
        {
            if (torrents.contains(check->torrentId))
                stopped.push_back(check->torrentId);
        }
        else if (std::holds_alternative<Task::Deletion>(task.work))
            log.Write("delete", "", "abandoned");
    }
    auto ids = unplanned;
    ids.insert(ids.end(), stopped.begin(), stopped.end());
    ids.insert(ids.end(), returning.begin(), returning.end());
    ids.insert(ids.end(), staged.begin(), staged.end());
    // A check that was abandoned stays pending: the torrent checks its files
    // once the person starts it. AbandonMove saves that a give-back's torrent
    // checks its files before the give-back changes one.
    CommitFacts(ids, [unplanned, stopped, returning, staged](std::string const& id, Facts& facts)
    {
        if (Contains(staged, id))
            facts.marker.reset();
        if (Contains(unplanned, id))
            facts.finalFolder.clear();
        if (Contains(stopped, id))
        {
            facts.intent = Intent::Paused;
            facts.verifyFiles = true;
        }
        if (Contains(returning, id) && facts.marker)
            facts.marker->stage = MoveStage::Returning;
    }, [this, ids, tasks, abandoned, done](Outcome outcome, auto const&)
    {
        // The asked work holds its torrents until the answer is saved, so
        // nothing abandoned starts before. A failed save asks again.
        if (outcome.error)
        {
            Ask(tasks);
            done(std::move(outcome));
            return;
        }
        for (auto const& id : Holders(tasks))
        {
            if (auto found = torrents.find(id); found != torrents.end())
                found->second.unfinished.reset();
        }
        // A Move that the person started meanwhile replaces the give-back.
        for (auto const& id : abandoned)
        {
            auto found = torrents.find(id);
            if (found != torrents.end() && found->second.facts.marker &&
                found->second.pathWork != PathWork::Move)
                JoinAbandonments(found->second);
        }
        for (auto const& id : ids)
        {
            if (auto found = torrents.find(id); found != torrents.end())
                found->second.ApplyIntent();
        }
        done(std::move(outcome));
    });
}

bool Engine::State::Abandoning(std::string const& id) const
{
    return std::any_of(abandonments.begin(), abandonments.end(),
        [&id](Abandonment const& abandonment) { return abandonment.torrentId == id; });
}

void Engine::State::JoinAbandonments(Torrent& torrent)
{
    if (!Abandoning(torrent.torrentId))
    {
        std::vector<FileWork::Use> uses;
        if (auto const& marker = torrent.facts.marker)
            uses = {{marker->destination, torrent.torrentId}, {torrent.facts.savePath, torrent.torrentId}};
        auto job = fileWork.Start(WorkKind::GiveBack, std::move(uses));
        job->turn = ++turns;
        abandonments.push_back({torrent.torrentId, std::move(job)});
    }
    // A rename that still runs keeps the files, and EndRename passes the
    // path work on when it ends.
    if (!torrent.pathWork)
    {
        torrent.pathWork = PathWork::Abandon;
        torrent.ApplyIntent();
    }
}

void Engine::State::AbandonMoves()
{
    if (IsShuttingDown())
        return;
    auto release = [](Torrent& torrent)
    {
        if (torrent.pathWork == PathWork::Abandon)
        {
            torrent.pathWork.reset();
            torrent.ApplyIntent();
        }
    };
    for (auto abandonment = abandonments.begin(); abandonment != abandonments.end();)
    {
        auto const& job = *abandonment->job;
        if (job.Phase() == JobPhase::Running)
        {
            ++abandonment;
            continue;
        }
        auto found = torrents.find(abandonment->torrentId);
        if (found == torrents.end())
        {
            // The removal waits for the path work to end, and the record of
            // the removed torrent's give-back takes over.
            if (auto removed = FindWorking(abandonment->torrentId))
                release(*removed);
            abandonment = abandonments.erase(abandonment);
            continue;
        }
        auto& torrent = found->second;
        // While its give-back waits for its root or for the person, the
        // torrent shows Moving, unless the person's waiting Move must show
        // why it does not start.
        if (job.Phase() != JobPhase::Queued)
        {
            if (torrent.waitingMove)
            {
                release(torrent);
                auto refusal = job.Failure() ? *job.Failure() :
                    Problem{ProblemKind::DownloadPathUnavailable, job.Unanswered()};
                if (torrent.waitingMove->refusal != refusal)
                {
                    torrent.waitingMove->refusal = std::move(refusal);
                    Invalidate(torrent.torrentId);
                }
            }
            ++abandonment;
            continue;
        }
        // After a Retry the give-back waits again, so its failure no longer
        // explains the waiting Move.
        if (torrent.waitingMove && torrent.waitingMove->refusal)
        {
            torrent.waitingMove->refusal.reset();
            Invalidate(torrent.torrentId);
        }
        if (!torrent.pathWork)
            JoinAbandonments(torrent);
        if (torrent.pathWork != PathWork::Abandon)
        {
            ++abandonment;
            continue;
        }
        auto const& marker = torrent.facts.marker;
        if (marker && (!GiveBackReady(torrent) || !GetsDrives(job)))
        {
            ++abandonment;
            continue;
        }
        if (!marker)
            release(torrent);
        else if (torrent.Paths().empty())
        {
            // Without its metadata the torrent cannot say which files the
            // move placed, so its marker stays for the person.
            release(torrent);
            log.Write("move", torrent.torrentId, "abandon_unknown");
        }
        else
        {
            // A save that fails at once can end the abandonment, so the
            // loop moves on first.
            AbandonMove(*abandonment++, torrent);
            continue;
        }
        abandonment = abandonments.erase(abandonment);
    }
}

bool Engine::State::GiveBackReady(Torrent const& torrent) const
{
    auto const& marker = torrent.facts.marker;
    return torrent.pathWork == PathWork::Abandon && marker && NamesReady(marker->destination) &&
        !torrent.control.Busy() && !torrent.NameWork();
}

std::optional<Engine::State::Task::Return> Engine::State::GiveBackOf(Torrent const& torrent) const
{
    auto const& marker = torrent.facts.marker;
    if (!marker || marker->stage == MoveStage::Staged)
        return std::nullopt;
    std::set<std::filesystem::path, decltype(&PathBefore)> found(PathBefore);
    for (auto const& path : marker->found)
        found.insert(FullPath(Wide(path)));
    Task::Return returned{torrent.torrentId, torrent.Name(), FullPath(Wide(marker->destination))};
    std::filesystem::path from = Wide(torrent.facts.savePath);
    for (auto const& path : torrent.Paths())
    {
        auto source = FullPath(from / path);
        auto target = FullPath(returned.root / path);
        if (!SamePath(source, target) && !found.contains(target))
            returned.files.push_back({std::move(source), std::move(target)});
    }
    if (returned.files.empty())
        return std::nullopt;
    return returned;
}

std::vector<Payload::File> Engine::State::Unshared(std::vector<Payload::File> files, std::string const& id) const
{
    std::set<std::filesystem::path, decltype(&PathBefore)> kept(PathBefore);
    for (auto const& [otherId, other] : torrents)
    {
        if (otherId == id)
            continue;
        auto used = Footprint(other);
        kept.insert(used.begin(), used.end());
    }
    std::erase_if(files, [&kept](Payload::File const& file) { return kept.contains(file.destination); });
    return files;
}

std::vector<Payload::File> Engine::State::Returned(Torrent const& torrent) const
{
    auto returned = GiveBackOf(torrent);
    return returned ? Unshared(std::move(returned->files), torrent.torrentId) : std::vector<Payload::File>{};
}

std::expected<GivenBack, std::string> Engine::State::GiveBack(std::vector<Payload::File> const& files,
    std::vector<std::string> const& folders, std::stop_token stop, std::atomic<std::uint64_t>& done)
{
    // Nothing to give back waits for no root.
    if (files.empty())
        return GivenBack::Deleted;
    auto present = [](auto const& path)
    {
        return std::filesystem::symlink_status(path).type() != std::filesystem::file_type::not_found;
    };
    auto given = GivenBack::Deleted;
    auto unanswered = Reach(folders, [&]
    {
        std::vector<std::filesystem::path> copies;
        for (auto const& file : files)
        {
            if (stop.stop_requested())
            {
                given = GivenBack::Stopped;
                return;
            }
            ++done;
            if (auto partial = Payload::Partial(file.destination); present(partial))
                copies.push_back(partial);
            if (!present(file.destination))
                continue;
            // Beside its source, only a complete copy is the move's.
            if (present(file.source))
            {
                if (Payload::IsCopy(file))
                    copies.push_back(file.destination);
                continue;
            }
            std::filesystem::create_directories(file.source.parent_path());
            if (!MoveFileExW(file.destination.c_str(), file.source.c_str(), 0))
                throw std::system_error(static_cast<int>(GetLastError()), std::system_category(),
                    Utf8(file.destination.wstring()));
            given = GivenBack::Renamed;
        }
        Erase(copies, DeletionMode::Permanent, {FullPath(Wide(folders.front()))}, stop);
        if (stop.stop_requested())
            given = GivenBack::Stopped;
    });
    if (unanswered)
        return std::unexpected(std::move(*unanswered));
    return given;
}

void Engine::State::AddGiveBack(Task::Return task)
{
    std::vector<FileWork::Use> uses{{Utf8(task.root.wstring()), task.torrentId}};
    if (!task.files.empty())
        uses.push_back({Utf8(task.files.front().source.parent_path().wstring()), task.torrentId});
    auto job = fileWork.Start(WorkKind::GiveBack, std::move(uses));
    returning.push_back({std::move(task), std::move(job)});
}

void Engine::State::GiveBackRemoved()
{
    if (IsShuttingDown())
        return;
    for (auto running = returning.begin(); running != returning.end(); ++running)
    {
        auto const& task = running->task;
        auto& job = *running->job;
        auto torrent = FindWorking(task.torrentId);
        // It waits while a try runs, while its root does not answer or its
        // failure waits for the person, while the torrent's move or rename
        // still runs, until the torrents whose files can lie at the
        // destination know their names, which say the files they keep. It
        // takes no drive turn, because the deletion of the removed torrent's
        // files waits for it and Delete files never waits for other work.
        if (job.Phase() != JobPhase::Queued || (torrent && torrent->FilesBusy()) ||
            !NamesReady(Utf8(task.root.wstring())))
            continue;
        job.Run();
        job.worker = std::make_shared<Store>(wake);
        job.progress = std::make_shared<Payload::Progress>();
        job.worker->Run([files = Unshared(task.files, task.torrentId), folders = job.Folders(),
            progress = job.progress, stop = exitStop.get_token()]
        {
            progress->placing = true;
            return GiveBack(files, folders, stop, progress->done);
        }, [this, running](GiveBackTry given)
        {
            auto end = EndTry(*running->job, running->retried, given);
            if (end == GiveBackEnd::Again || end == GiveBackEnd::Failed)
                log.Write("move", running->task.torrentId, given ? "return_unreachable" : given.error().detail);
            // The record outlives every other end: the deletion of the
            // torrent's files waits for it.
            if (end != GiveBackEnd::Ended)
                return;
            returning.erase(running);
            ContinueDeletion();
        });
    }
}

void Engine::State::AbandonMove(Abandonment& abandonment, Torrent& torrent)
{
    auto const& marker = *torrent.facts.marker;
    // A staged move changed no file.
    auto files = marker.stage == MoveStage::Staged ? std::vector<Payload::File>{} : Returned(torrent);
    auto& job = *abandonment.job;
    job.Run();
    job.worker = std::make_shared<Store>(wake);
    job.progress = std::make_shared<Payload::Progress>();
    auto run = [this, id = torrent.torrentId, worker = job.worker, progress = job.progress,
        files = std::move(files), folders = job.Folders()]
    {
        worker->Run([this, handle = Working(id).handle, files, folders, progress, stop = exitStop.get_token()]
        {
            ReleaseFiles({handle});
            progress->placing = true;
            return GiveBack(files, folders, stop, progress->done);
        }, [this, id](GiveBackTry given)
        {
            EndAbandonment(id, std::move(given));
        });
    };
    // The record of a give-back past Staged says that the torrent checks its
    // files before the give-back changes one, so an end after its renames
    // still checks them.
    if (marker.stage == MoveStage::Staged || torrent.facts.verifyFiles)
    {
        run();
        return;
    }
    // A save that fails ends the try as a give-back that fails does.
    auto fail = [this](std::string const& id, Outcome const& outcome)
    {
        log.Write("move", id, outcome.detail);
        EndAbandonment(id, std::unexpected(StorageOutcome{
            .failure = outcome.error == ErrorCode::Overloaded ? StorageFailure::Overloaded : StorageFailure::Failed,
            .detail = outcome.detail}));
    };
    // A torrent removed meanwhile gives its files back as its removal's
    // record, and the next pass ends its abandonment.
    auto removed = [this](std::string const& id)
    {
        auto found = std::find_if(abandonments.begin(), abandonments.end(),
            [&id](Abandonment const& entry) { return entry.torrentId == id; });
        if (found != abandonments.end())
            found->job->Queue();
    };
    if (!changes.Queue([this, id = torrent.torrentId, run, fail, removed]
    {
        if (!torrents.contains(id))
        {
            removed(id);
            return;
        }
        CommitFacts({id}, [](std::string const&, Facts& facts) { facts.verifyFiles = true; },
            [this, id, run, fail, removed](Outcome outcome, auto const&)
        {
            if (outcome.error)
                fail(id, outcome);
            else if (!torrents.contains(id))
                removed(id);
            else
                run();
        });
    }))
        fail(torrent.torrentId, Outcome{ErrorCode::Overloaded});
}

void Engine::State::EndAbandonment(std::string const& id, GiveBackTry given)
{
    auto abandonment = std::find_if(abandonments.begin(), abandonments.end(),
        [&id](Abandonment const& entry) { return entry.torrentId == id; });
    if (abandonment == abandonments.end())
        return;
    auto end = EndTry(*abandonment->job, abandonment->retried, given);
    // The marker says that the files go back, so the next start continues.
    if (end == GiveBackEnd::Stopped)
        return;
    auto torrent = FindWorking(id);
    bool listed = torrents.contains(id);
    // A listed torrent's give-back that runs again, or whose failure waits
    // for the answer to the person's waiting Move, keeps its record and its
    // path work, and AbandonMoves goes on with it.
    if (torrent && listed && (end == GiveBackEnd::Again || (end == GiveBackEnd::Failed && torrent->waitingMove)))
    {
        log.Write("move", id, end == GiveBackEnd::Failed ? "give_back_failed" :
            abandonment->job->Phase() == JobPhase::Unanswered ? "abandon_unreachable" : "give_back_retry");
        return;
    }
    abandonments.erase(abandonment);
    if (!torrent)
        return;
    // A removed torrent's give-back that did not end goes on as its
    // removal's record.
    if (end == GiveBackEnd::Again)
    {
        torrent->pathWork.reset();
        torrent->ApplyIntent();
        log.Write("move", id, "abandon_unreachable");
        return;
    }
    std::optional<Problem> failure;
    if (end == GiveBackEnd::Failed)
        failure = Problem{ProblemKind::MoveInterrupted, given ? std::string() : given.error().detail};
    auto settle = [this, id](std::optional<Problem> problem)
    {
        if (auto torrent = FindWorking(id))
        {
            EndMove(*torrent, std::move(problem), false);
            torrent->ApplyIntent();
        }
    };
    // A torrent removed meanwhile left the saved list with its marker.
    if (!changes.Queue([this, id, renamed = given && *given && **given == GivenBack::Renamed, failure, settle]
    {
        if (!torrents.contains(id))
        {
            settle(failure);
            return;
        }
        CommitFacts({id}, [renamed, failed = failure.has_value()](std::string const&, Facts& facts)
        {
            if (!facts.marker)
                return;
            if (failed)
            {
                // The copies may be gone, so their proof goes too.
                facts.marker->failed = true;
                facts.marker->copy.reset();
                return;
            }
            facts.marker.reset();
            facts.verifyFiles |= renamed;
        }, [settle, failure](Outcome outcome, auto const&)
        {
            settle(outcome.error ? std::optional(Problem{ProblemKind::MoveInterrupted, outcome.detail}) :
                failure);
        });
    }))
        settle(Problem{ProblemKind::StorageOverloaded});
}

GiveBackEnd Engine::State::EndTry(FileWork::Job& job, bool& retried, GiveBackTry const& given)
{
    // The job still runs until the next start continues it.
    if (given && *given && **given == GivenBack::Stopped)
        return GiveBackEnd::Stopped;
    // A root that went away starts a new episode, with its own retry.
    if (given && !*given)
    {
        job.Await(given->error());
        retried = false;
        return GiveBackEnd::Again;
    }
    job.Queue();
    if (given)
        return GiveBackEnd::Ended;
    // A held file or a refused access while every root answers is no matter
    // of the root, so no probe answers it: one retry at once, and then the
    // person decides.
    if (!std::exchange(retried, true))
        return GiveBackEnd::Again;
    auto const& error = given.error();
    auto failure = error.cause ? Problem::From(error.cause) : Problem::From(error, ProblemKind::TorrentError);
    if (failure.detail.empty())
        failure.detail = error.detail;
    job.Fail(std::move(failure));
    return GiveBackEnd::Failed;
}

void Engine::State::RetryGiveBack(std::string const& id)
{
    for (auto& abandonment : abandonments)
    {
        if (abandonment.torrentId == id && abandonment.job->Phase() == JobPhase::Failed)
            abandonment.job->Queue();
    }
}

void Engine::State::RetryGiveBacks()
{
    for (auto& running : returning)
    {
        if (running.job->Phase() == JobPhase::Failed)
            running.job->Queue();
    }
}

bool Engine::State::GiveBackFailed(std::string const& id) const
{
    return std::any_of(abandonments.begin(), abandonments.end(), [&id](Abandonment const& abandonment)
        { return abandonment.torrentId == id && abandonment.job->Phase() == JobPhase::Failed; });
}

Json Engine::State::AskedCounts() const
{
    if (asked.empty())
        return nullptr;
    std::size_t moves = 0;
    std::size_t checks = 0;
    // A torrent whose files and .torrent file both wait counts once.
    std::set<std::string> deleting;
    std::size_t unnamed = 0;
    for (auto const& task : asked)
    {
        if (auto move = std::get_if<Task::Move>(&task.work))
            moves += move->ids.size();
        else if (std::holds_alternative<Task::Check>(task.work))
            ++checks;
        else if (auto deletion = std::get_if<Task::Deletion>(&task.work))
        {
            deleting.insert(deletion->ids.begin(), deletion->ids.end());
            unnamed += deletion->ids.empty();
        }
    }
    auto deleted = deleting.size() + unnamed;
    return {{"moves", moves}, {"checks", checks}, {"deletions", deleted}};
}
}
