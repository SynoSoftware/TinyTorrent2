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
    if (exitTasks)
        return *exitTasks;
    auto tasks = asked;
    auto held = Holders(asked);
    // A torrent removed during its move keeps its marker until the save of
    // its new folder, and until then a cut gives its files back to its save
    // folder.
    auto givesBack = [this](std::string const& id)
    {
        auto found = removed.find(id);
        if (found == removed.end())
            return false;
        auto const& marker = found->second.torrent.facts.marker;
        return marker && marker->stage != MoveStage::Staged;
    };
    for (auto const& moving : moves)
    {
        // A give-back needs the sources, so they go only once that save has
        // cleared the marker.
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
    // An interrupted move: a marker that no move holds. A move that failed
    // by itself waits for the person instead, and one that gives its files
    // back continues at each start.
    auto interrupted = tasks.size();
    for (auto const& [id, torrent] : torrents)
    {
        auto const& marker = torrent.facts.marker;
        if (marker && !marker->failed && marker->stage != MoveStage::Returning && !torrent.waitingMove &&
            held.insert(id).second)
            AddMove(tasks, interrupted, id, marker->destination);
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
        tasks.push_back({running});
    if (NamesReady())
    {
        for (auto const& [id, removal] : removed)
        {
            if (givesBack(id))
                tasks.push_back({Task::Return{id, removal.torrent.Name(),
                    FullPath(Wide(removal.torrent.facts.marker->destination)), Returned(removal.torrent)}});
        }
    }
    for (auto const& deletion : deletions)
    {
        if (deletion.mode)
            tasks.push_back({Task::Deletion{deletion.ids, *deletion.mode, deletion.files, deletion.roots,
                deletion.names}});
    }
    if (!removed.empty() && NamesReady())
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
    Task::Deletion deletion{ids, mode};
    for (auto const& file : scope.files)
    {
        if (!std::binary_search(scope.kept.begin(), scope.kept.end(), file, PathBefore))
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
        if (!moving.progress || moving.current >= moving.ids.size())
            continue;
        auto proof = moving.progress->Proven();
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
        {
            abandonments.push_back({id});
            torrent.unfinished = TaskKind::Move;
        }
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
    if (settings.unfinished == TaskAnswer::Abandon)
    {
        if (!changes.Queue([this, tasks] { AbandonTasks(tasks, [](Outcome) {}); }))
            log.Write("startup", "", "storage_overloaded");
        return;
    }
    // The rows show the waiting work without a toast for each torrent.
    asked = std::move(tasks);
    for (auto const& task : asked)
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
}

void Engine::State::Answer(TaskAnswer answer, std::function<void(Outcome)> completion)
{
    if (!changes.Queue([this, answer, completion]
    {
        auto tasks = std::exchange(asked, {});
        auto ids = Holders(tasks);
        for (auto const& id : ids)
        {
            if (auto found = torrents.find(id); found != torrents.end())
                found->second.unfinished.reset();
        }
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
        ContinueTasks(tasks);
        changes.Commit([document = Saved()] { return document.ToJson(); }, done);
    }))
    {
        completion({ErrorCode::Overloaded});
    }
}

void Engine::State::DropTorrents(std::vector<Task>& tasks, std::vector<std::string> const& ids)
{
    std::erase_if(tasks, [&ids](Task& task)
    {
        if (auto move = std::get_if<Task::Move>(&task.work))
        {
            std::erase_if(move->ids, [&ids](auto const& id) { return Contains(ids, id); });
            return move->ids.empty();
        }
        auto check = std::get_if<Task::Check>(&task.work);
        return check && Contains(ids, check->torrentId);
    });
}

void Engine::State::Unask(std::vector<std::string> const& ids)
{
    DropTorrents(asked, ids);
    for (auto const& id : ids)
    {
        if (auto found = torrents.find(id); found != torrents.end())
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
                if (found != torrents.end() && !found->second.moving)
                    found->second.waitingMove = WaitingMove{move->destination, turn};
            }
        }
        else if (auto check = std::get_if<Task::Check>(&task.work))
        {
            if (auto found = torrents.find(check->torrentId); found != torrents.end())
                found->second.needsRecheck = true;
        }
        else if (auto returned = std::get_if<Task::Return>(&task.work))
            GiveBackRemoved(*returned);
        else
        {
            auto const& deleting = std::get<Task::Deletion>(task.work);
            Deletion deletion;
            deletion.ids = deleting.ids;
            deletion.mode = deleting.mode;
            deletion.holds = deleting.files;
            std::sort(deletion.holds.begin(), deletion.holds.end(), PathBefore);
            deletion.files = deleting.files;
            deletion.roots = deleting.roots;
            deletion.names = deleting.names;
            deletion.phase = DeletionPhase::Waiting;
            deletions.push_back(std::move(deletion));
        }
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
                if (torrent.facts.marker)
                {
                    if (std::none_of(abandonments.begin(), abandonments.end(),
                        [&id](Abandonment const& abandonment) { return abandonment.torrentId == id; }))
                        abandonments.push_back({id});
                    torrent.unfinished = TaskKind::Move;
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
    // A check that was abandoned stays pending: the torrent checks its files
    // once the person starts it.
    CommitFacts(ids, [unplanned, stopped, returning](std::string const& id, Facts& facts)
    {
        if (Contains(unplanned, id))
            facts.finalFolder.clear();
        if (Contains(stopped, id))
        {
            facts.intent = Intent::Paused;
            facts.verifyFiles = true;
        }
        if (Contains(returning, id) && facts.marker)
            facts.marker->stage = MoveStage::Returning;
    }, [this, ids, done](Outcome outcome, auto const&)
    {
        for (auto const& id : ids)
        {
            if (auto found = torrents.find(id); found != torrents.end())
                found->second.ApplyIntent();
        }
        done(std::move(outcome));
    });
}

void Engine::State::AbandonMoves()
{
    if (IsShuttingDown())
        return;
    for (auto abandonment = abandonments.begin(); abandonment != abandonments.end();)
    {
        if (abandonment->worker)
        {
            ++abandonment;
            continue;
        }
        auto found = torrents.find(abandonment->torrentId);
        if (found == torrents.end())
        {
            abandonment = abandonments.erase(abandonment);
            continue;
        }
        auto& torrent = found->second;
        auto const& marker = torrent.facts.marker;
        if (marker)
        {
            auto needed = FindDrives(Folders({torrent.torrentId}, marker->destination));
            if (!NamesReady() || torrent.FilesBusy() || !needed ||
                Blocked(*needed, DrivesInUse(), {torrent.torrentId}))
            {
                ++abandonment;
                continue;
            }
        }
        if (!marker)
            torrent.unfinished.reset();
        else if (torrent.Paths().empty())
        {
            // Without its metadata the torrent cannot say which files the
            // move placed, so its marker stays for the person.
            torrent.unfinished.reset();
            log.Write("move", torrent.torrentId, "abandon_unknown");
        }
        else
        {
            AbandonMove(*abandonment, torrent);
            ++abandonment;
            continue;
        }
        abandonment = abandonments.erase(abandonment);
    }
}

std::vector<Payload::File> Engine::State::Returned(Torrent const& torrent) const
{
    auto const& marker = *torrent.facts.marker;
    std::set<std::filesystem::path, decltype(&PathBefore)> kept(PathBefore);
    for (auto const& path : marker.found)
        kept.insert(FullPath(Wide(path)));
    for (auto const& [id, other] : torrents)
    {
        if (id == torrent.torrentId)
            continue;
        auto used = Footprint(other);
        kept.insert(used.begin(), used.end());
    }
    std::vector<Payload::File> files;
    std::filesystem::path from = Wide(torrent.facts.savePath);
    std::filesystem::path to = Wide(marker.destination);
    for (auto const& path : torrent.Paths())
    {
        auto source = FullPath(from / path);
        auto target = FullPath(to / path);
        if (!SamePath(source, target) && !kept.contains(target))
            files.push_back({std::move(source), std::move(target)});
    }
    return files;
}

GivenBack Engine::State::GiveBack(std::vector<Payload::File> const& files, std::filesystem::path const& root,
    std::stop_token stop, std::atomic<std::uint64_t>& done)
{
    if (!Reachable(root))
        return GivenBack::Unreachable;
    auto present = [](auto const& path)
    {
        return std::filesystem::symlink_status(path).type() != std::filesystem::file_type::not_found;
    };
    std::vector<std::filesystem::path> copies;
    bool renamed = false;
    for (auto const& file : files)
    {
        if (stop.stop_requested())
            return GivenBack::Stopped;
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
        renamed = true;
    }
    Erase(copies, DeletionMode::Permanent, {root}, stop);
    if (stop.stop_requested())
        return GivenBack::Stopped;
    return renamed ? GivenBack::Renamed : GivenBack::Deleted;
}

void Engine::State::GiveBackRemoved(Task::Return returned)
{
    auto running = returning.insert(returning.end(), std::move(returned));
    payload.Run([files = running->files, root = running->root, stop = exitStop.get_token()]
    {
        std::atomic<std::uint64_t> done{0};
        return GiveBack(files, root, stop, done);
    }, [this, running](std::expected<GivenBack, StorageOutcome> given)
    {
        // Exit stopped it, and the saved task continues it at the next start.
        if (given && *given == GivenBack::Stopped)
            return;
        if (given && *given == GivenBack::Unreachable)
        {
            log.Write("move", running->torrentId, "return_unreachable");
            return;
        }
        if (!given)
            log.Write("move", running->torrentId, given.error().detail);
        returning.erase(running);
        ContinueDeletion();
    });
}

void Engine::State::AbandonMove(Abandonment& abandonment, Torrent& torrent)
{
    auto const& marker = *torrent.facts.marker;
    // A staged move changed no file.
    auto files = marker.stage == MoveStage::Staged ? std::vector<Payload::File>{} : Returned(torrent);
    torrent.moving = true;
    torrent.ApplyIntent();
    abandonment.worker = std::make_shared<Store>(wake);
    abandonment.done = std::make_shared<std::atomic<std::uint64_t>>(0);
    abandonment.worker->Run([this, handle = torrent.handle, files, root = FullPath(Wide(marker.destination)),
        done = abandonment.done, stop = exitStop.get_token()]
    {
        ReleaseFiles({handle});
        return GiveBack(files, root, stop, *done);
    }, [this, id = torrent.torrentId](std::expected<GivenBack, StorageOutcome> given)
    {
        // The marker says that the files go back, so the next start continues.
        if (given && *given == GivenBack::Stopped)
            return;
        abandonments.remove_if([&id](Abandonment const& abandonment) { return abandonment.torrentId == id; });
        auto torrent = FindWorking(id);
        if (!torrent)
            return;
        torrent->unfinished.reset();
        // The marker stays, so the next start gives the files back again.
        if (given && *given == GivenBack::Unreachable)
        {
            torrent->moving = false;
            torrent->ApplyIntent();
            log.Write("move", id, "abandon_unreachable");
            return;
        }
        std::optional<Problem> failure;
        if (!given)
            failure = Problem{ProblemKind::MoveInterrupted, given.error().detail};
        auto settle = [this, id](std::optional<Problem> problem)
        {
            if (auto torrent = FindWorking(id))
            {
                EndMove(*torrent, std::move(problem), false);
                torrent->ApplyIntent();
            }
        };
        // A torrent removed meanwhile left the saved list with its marker.
        // The work that Exit saved no longer holds a move that is given back
        // or failed.
        if (!changes.Queue([this, id, renamed = given && *given == GivenBack::Renamed, failure, settle]
        {
            if (!torrents.contains(id))
            {
                settle(failure);
                return;
            }
            if (exitTasks)
                DropTorrents(*exitTasks, {id});
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
    });
}

Json Engine::State::AskedCounts() const
{
    if (asked.empty())
        return nullptr;
    std::size_t moves = 0;
    std::size_t checks = 0;
    std::size_t deleted = 0;
    for (auto const& task : asked)
    {
        if (auto move = std::get_if<Task::Move>(&task.work))
            moves += move->ids.size();
        else if (std::holds_alternative<Task::Check>(task.work))
            ++checks;
        else if (auto deletion = std::get_if<Task::Deletion>(&task.work))
            deleted += std::max<std::size_t>(1, deletion->ids.size());
    }
    return {{"moves", moves}, {"checks", checks}, {"deletions", deleted}};
}
}
