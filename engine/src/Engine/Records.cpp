#include "Engine/State.h"
#include "Gozler.h"
#include <Windows.h>
#include <algorithm>

namespace tt
{
namespace
{
// A records file is written at most this often per torrent, because files
// finish one after another while a torrent downloads.
constexpr auto recordInterval = std::chrono::seconds(30);

// The folder that holds `located` as `relative`, or nothing when the path
// does not end with those parts.
std::optional<std::filesystem::path> Holder(std::filesystem::path const& located, std::filesystem::path const& relative)
{
    std::vector<std::filesystem::path> tail(relative.begin(), relative.end());
    auto folder = located;
    for (auto part = tail.rbegin(); part != tail.rend(); ++part)
    {
        if (!folder.has_relative_path() ||
            CompareStringOrdinal(folder.filename().c_str(), -1, part->c_str(), -1, TRUE) != CSTR_EQUAL)
        {
            return std::nullopt;
        }
        folder = folder.parent_path();
    }
    return folder;
}
}

std::filesystem::path Engine::State::RecordsFile(std::string const& id) const
{
    return directory / Wide(id + ".records");
}

void Engine::State::ContinueRecords()
{
    for (auto& [id, torrent] : torrents)
    {
        // The pass waits until the files have their final names and no work
        // changes them, so a record describes the file that stays.
        if (torrent.recording != PassPhase::Wanted || torrent.waiting || torrent.kept || torrent.FileWork() ||
            torrent.control.Running() || torrent.control.AwaitsReload() || torrent.namePhase != NamePhase::Ready ||
            !torrent.completedFiles.empty() || torrent.ChecksFiles(false) || !torrent.status.has_metadata)
        {
            continue;
        }
        torrent.recording = PassPhase::Running;
        std::set<lt::file_index_t> known;
        for (auto const& [file, record] : torrent.records)
            known.insert(file);
        VolumeWorker(torrent.facts.savePath).Run([handle = torrent.handle, known, layout = torrent.facts.layout]
        {
            Records taken;
            auto status = handle.status(lt::torrent_handle::query_save_path | lt::torrent_handle::query_torrent_file |
                lt::torrent_handle::query_renamed_files | lt::torrent_handle::query_pieces);
            auto metadata = status.torrent_file.lock();
            if (!metadata)
                return taken;
            auto const& files = metadata->layout();
            for (auto index : files.file_range())
            {
                if (files.pad_file_at(index) || known.contains(index) || !Complete(*metadata, index, status.pieces) ||
                    IsUnfinished(status.renamed_files.file_path(files, index), ContentPath(files, index, layout)))
                {
                    continue;
                }
                auto path = FullPath(Wide(status.renamed_files.file_path(files, index, status.save_path)));
                if (auto record = Record::Take(path))
                    taken.emplace(index, *record);
            }
            return taken;
        }, [this, id, handle = torrent.handle](std::expected<Records, StorageOutcome> taken)
        {
            auto found = torrents.find(id);
            if (found == torrents.end())
                return;
            auto& torrent = found->second;
            // Records only add, so a pass wanted again while this one ran
            // keeps this one's records and runs after it.
            torrent.recording = torrent.recording == PassPhase::Again ? PassPhase::Wanted : PassPhase::Idle;
            if (!taken)
            {
                log.Write("record", id, taken.error().detail);
                return;
            }
            if (torrent.handle != handle)
                return;
            // A record taken meanwhile by another pass stays. The file is
            // written again either way, because it says whether a pass is due.
            torrent.records.merge(*taken);
            torrent.recordsUnsaved = true;
            Invalidate(id);
        });
    }
}

void Engine::State::SaveRecords(bool all)
{
    auto now = std::chrono::steady_clock::now();
    for (auto& [id, torrent] : torrents)
    {
        if (!torrent.recordsUnsaved || (!all && now - torrent.recordsSaved < recordInterval))
            continue;
        torrent.recordsUnsaved = false;
        torrent.recordsSaved = now;
        // A write that fails costs a check later, not data, so it is only
        // logged and written again at the next change.
        auto saved = ToJson(SavedRecords{torrent.records, torrent.recording != PassPhase::Idle});
        store.Write(RecordsFile(id), [saved = std::move(saved)] { return saved.dump(); },
            [this, id](StorageOutcome outcome)
        {
            if (outcome.failure)
                log.Write("record", id, outcome.detail);
        }, resumeLimit);
    }
}

std::set<lt::file_index_t> Engine::State::Holding(lt::torrent_info const& metadata,
    lt::typed_bitfield<lt::piece_index_t> const& pieces, std::vector<lt::download_priority_t> const& priorities,
    Facts::Question const& asked)
{
    std::set<lt::file_index_t> holding;
    auto const& layout = metadata.layout();
    for (auto index : layout.file_range())
    {
        auto item = static_cast<std::size_t>(static_cast<int>(index));
        if (layout.pad_file_at(index))
            continue;
        bool skipped = item < priorities.size() && priorities[item] == lt::dont_download;
        if (asked.files.contains(index) || HeldData(layout, index, pieces, skipped) > 0)
            holding.insert(index);
    }
    return holding;
}

std::set<lt::file_index_t> Engine::State::Prove(lt::add_torrent_params const& params,
    std::set<lt::file_index_t> const& files, Records const& records, bool follows)
{
    std::set<lt::file_index_t> proven;
    auto const& layout = params.ti->layout();
    for (auto index : files)
    {
        auto record = records.find(index);
        if (record == records.end())
            continue;
        // The resume data names only the files that libtorrent renamed.
        auto renamed = params.renamed_files.find(index);
        auto name = renamed != params.renamed_files.end() ? renamed->second : layout.file_path(index);
        auto path = FullPath(std::filesystem::path(Wide(params.save_path)) / Wide(name));
        auto size = layout.file_size(index);
        if (record->second.Proves(path, size) || (follows && record->second.Matches(path, size)))
            proven.insert(index);
    }
    return proven;
}

void Engine::State::Lookout::Answer() const
{
    Json torrents = Json::array();
    for (auto const& [id, folder] : folders)
        torrents.push_back({{"torrent_id", id}, {"folder", folder ? Json(*folder) : Json()}});
    reply(Success({{"torrents", std::move(torrents)}}));
}

void Engine::State::EndLookouts(std::string const& id, std::optional<std::string> const& folder)
{
    auto found = lookouts.find(id);
    if (found == lookouts.end())
        return;
    auto waiting = std::move(found->second);
    lookouts.erase(found);
    for (auto const& lookout : waiting)
    {
        lookout->folders[id] = folder;
        if (--lookout->pending == 0)
            lookout->Answer();
    }
}

void Engine::State::ContinueSearches()
{
    // A removed torrent's look commands end without a folder.
    std::vector<std::string> gone;
    for (auto const& [id, waiting] : lookouts)
    {
        if (!torrents.contains(id))
            gone.push_back(id);
    }
    for (auto const& id : gone)
        EndLookouts(id, std::nullopt);
    auto now = std::chrono::steady_clock::now();
    for (auto& [id, torrent] : torrents)
    {
        if (torrent.search != PassPhase::Wanted || now < torrent.searchAt)
            continue;
        // A torrent without metadata has no file to find. One whose drive
        // does not answer keeps its search until the drive is back, but its
        // look commands end now without a folder.
        if (torrent.waiting || !torrent.status.has_metadata)
        {
            if (!torrent.waiting)
            {
                torrent.search = PassPhase::Idle;
                torrent.answer.reset();
            }
            EndLookouts(id, std::nullopt);
            continue;
        }
        // The search waits while other work uses the files or a look or a
        // reload is due, which change what the question holds.
        if (torrent.FileWork() || torrent.control.Running() || torrent.control.AwaitsReload() ||
            torrent.look == LookPhase::Wanted || torrent.look == LookPhase::Running || torrent.waitingMove)
        {
            continue;
        }
        if (torrent.facts.Asked().files.empty())
        {
            torrent.search = PassPhase::Idle;
            torrent.answer.reset();
            EndLookouts(id, std::nullopt);
            continue;
        }
        torrent.search = PassPhase::Running;
        VolumeWorker(torrent.facts.savePath).Run(
            [handle = torrent.handle, kept = torrent.kept, asked = torrent.facts.Asked(), records = torrent.records,
            folder = torrent.facts.savePath]() -> std::optional<Searched>
        {
            Searched searched;
            if (Reach({folder}, [&] { searched = SearchDrive(handle, kept, asked, records); }))
                return std::nullopt;
            return searched;
        }, [this, id, handle = torrent.handle](std::expected<std::optional<Searched>, StorageOutcome> searched)
        {
            if (!searched)
                log.Write("search", id, searched.error().detail);
            EndSearch(id, handle, searched ? std::move(*searched) : std::nullopt);
        });
    }
}

Engine::State::Searched Engine::State::SearchDrive(lt::torrent_handle const& handle,
    std::optional<lt::typed_bitfield<lt::piece_index_t>> kept, Facts::Question const& asked, Records const& records)
{
    Searched searched;
    auto status = handle.status(lt::torrent_handle::query_save_path | lt::torrent_handle::query_torrent_file |
        lt::torrent_handle::query_renamed_files | lt::torrent_handle::query_pieces);
    auto metadata = status.torrent_file.lock();
    if (!metadata)
        return searched;
    auto const& layout = metadata->layout();
    auto at = [&](lt::file_index_t index, std::string const& folder)
    {
        return FullPath(Wide(status.renamed_files.file_path(layout, index, folder)));
    };
    // Files put back in their place, for example from the Recycle Bin.
    for (auto const& [file, priority] : asked.files)
    {
        if (auto record = records.find(file);
            record != records.end() && record->second.Proves(at(file, status.save_path), layout.file_size(file)))
        {
            searched.back.push_back(file);
        }
    }
    if (!searched.back.empty())
        return searched;
    // Every file that held data left its place: none of them is there, changed
    // or not, and each has its record.
    std::map<lt::file_index_t, std::filesystem::path> moved;
    for (auto file : Holding(*metadata, kept ? *kept : status.pieces, handle.get_file_priorities(), asked))
    {
        std::error_code error;
        if (std::filesystem::exists(at(file, status.save_path), error) || !records.contains(file))
            return searched;
        moved.emplace(file, Wide(status.renamed_files.file_path(layout, file)));
    }
    // On its drive, each file's ID finds it, and the torrent follows when one
    // other folder holds every file at the path the torrent gives it.
    // Otherwise gozler may find the payload on another drive.
    std::optional<std::filesystem::path> holder;
    for (auto const& [file, relative] : moved)
    {
        auto located = records.at(file).Locate(Wide(status.save_path));
        auto folder = located ? Holder(*located, relative) : std::nullopt;
        if (!folder || (holder && !SamePath(*holder, *folder)))
        {
            holder.reset();
            break;
        }
        holder = std::move(folder);
    }
    if (holder && !SamePath(*holder, FullPath(Wide(status.save_path))))
        searched.folder = Utf8(holder->wstring());
    else
        searched.moved = std::move(moved);
    return searched;
}

void Engine::State::EndSearch(std::string const& id, lt::torrent_handle const& handle, std::optional<Searched> searched)
{
    auto found = torrents.find(id);
    if (found == torrents.end())
        return;
    auto& torrent = found->second;
    // A request since the search started, or a reload that changed the pieces
    // it judged, makes it run again, and this result may predate either.
    if (torrent.search == PassPhase::Again || torrent.handle != handle)
    {
        torrent.search = PassPhase::Wanted;
        return;
    }
    // gozler may find a payload that moved to another drive, unless the person
    // already answered.
    if (searched && searched->back.empty() && !searched->folder && !searched->moved.empty() && !torrent.answer &&
        !IsShuttingDown())
    {
        Seek(torrent, std::move(searched->moved));
        return;
    }
    Settle(torrent, searched);
}

void Engine::State::Settle(Torrent& torrent, std::optional<Searched> const& searched)
{
    auto const& id = torrent.torrentId;
    torrent.search = PassPhase::Idle;
    torrent.searched = torrent.facts.Asked().number;
    auto answer = std::exchange(torrent.answer, std::nullopt);
    // What each search decided, which the snapshot does not show.
    auto recorded = std::count_if(torrent.facts.Asked().files.begin(), torrent.facts.Asked().files.end(),
        [&torrent](auto const& entry) { return torrent.records.contains(entry.first); });
    log.Write("search", id, !searched ? std::string("unanswered") :
        "back " + std::to_string(searched->back.size()) + " follow " + std::to_string(searched->folder.has_value()) +
        " recorded " + std::to_string(recorded) + "/" + std::to_string(torrent.facts.Asked().files.size()));
    // A torrent whose drive did not answer keeps its question, and the
    // answer that waited for the search does not apply.
    if (!searched)
    {
        EndLookouts(id, std::nullopt);
        return;
    }
    std::optional<std::string> follows;
    if (!searched->back.empty())
    {
        torrent.returned.insert(searched->back.begin(), searched->back.end());
        Steer(torrent, Control::Looked{{}, true});
    }
    else if (searched->folder && !torrent.MoveBlocked() &&
        !(torrent.failedFollow && SameFolder(*torrent.failedFollow, *searched->folder)))
    {
        // The follow takes its turn for its drives as any move does, and
        // download mode proves the files at the new folder before it checks.
        // It answers the question itself.
        log.Write("follow", id, "found");
        torrent.waitingMove = WaitingMove{.destination = *searched->folder, .turn = ++turns,
            .cause = MoveCause::Follow, .mode = MoveMode::Download};
        follows = searched->folder;
        answer.reset();
    }
    EndLookouts(id, follows);
    if (answer)
    {
        // The answer applies through the person's edit of the priorities,
        // to the files still in the question that are not back.
        std::vector<lt::file_index_t> files;
        for (auto file : answer->files)
        {
            if (torrent.facts.Asked().files.contains(file) &&
                std::find(searched->back.begin(), searched->back.end(), file) == searched->back.end())
            {
                files.push_back(file);
            }
        }
        auto action = answer->choice == MissingAnswer::Redownload ? FileAction(Want{}) : FileAction(lt::dont_download);
        if (!files.empty() && !changes.Queue([this, id, choice = ResolvedChoice{std::move(files), action}]
        {
            if (torrents.contains(id))
            {
                CommitEdit(id, {choice}, std::nullopt, [this, id](Outcome outcome)
                {
                    if (outcome.error)
                        log.Write("answer", id, ToString(*outcome.error));
                });
            }
        }))
        {
            log.Write("answer", id, ToString(ErrorCode::Overloaded));
        }
    }
    Invalidate(id);
    statusPending = true;
}

void Engine::State::Seek(Torrent& torrent, std::map<lt::file_index_t, std::filesystem::path> moved)
{
    auto metadata = torrent.Metadata();
    if (!metadata)
    {
        Settle(torrent, Searched{});
        return;
    }
    // At most five files, the largest first, each by its whole name and size.
    std::vector<Gozler::Wanted> wanted;
    for (auto const& [file, relative] : moved)
        wanted.push_back({relative.filename().wstring(), metadata->layout().file_size(file)});
    std::sort(wanted.begin(), wanted.end(), [](auto const& left, auto const& right) { return left.size > right.size; });
    wanted.resize(std::min<std::size_t>(wanted.size(), 5));
    Records records;
    std::map<lt::file_index_t, std::int64_t> sizes;
    for (auto const& [file, relative] : moved)
    {
        records.emplace(file, torrent.records.at(file));
        sizes.emplace(file, metadata->layout().file_size(file));
    }
    finder.Run([wanted, stop = exitStop.get_token()]() -> std::optional<Gozler::Answer>
    {
        auto program = Gozler::Program();
        if (!program)
            return std::nullopt;
        return Gozler::Find(*program, wanted, stop);
    }, [this, id = torrent.torrentId, number = torrent.facts.Asked().number, origin = torrent.facts.savePath,
        moved, records, sizes](std::expected<std::optional<Gozler::Answer>, StorageOutcome> answer)
    {
        if (!answer || !*answer)
        {
            EndSeek(id, Sought{number, origin});
            return;
        }
        // Each result, less the file's path inside the torrent, names a folder
        // that may hold the payload.
        std::set<std::filesystem::path, decltype(&PathBefore)> folders(PathBefore);
        for (auto const& path : (*answer)->found)
        {
            for (auto const& [file, relative] : moved)
            {
                auto folder = Holder(path, relative);
                if (folder && !SamePath(*folder, FullPath(Wide(origin))))
                    folders.insert(*folder);
            }
        }
        bool building = (*answer)->building;
        if (folders.empty())
        {
            EndSeek(id, Sought{number, origin, {}, building});
            return;
        }
        // Each folder is checked on its own drive's worker: it must hold every
        // file with its record's size and time.
        struct Checked
        {
            std::size_t pending = 0;
            std::vector<std::string> passed;
        };
        auto checked = std::make_shared<Checked>(Checked{folders.size()});
        for (auto const& folder : folders)
        {
            auto text = Utf8(folder.wstring());
            VolumeWorker(text).Run([folder, moved, records, sizes]
            {
                return std::all_of(moved.begin(), moved.end(), [&](auto const& entry)
                {
                    return records.at(entry.first).Matches(FullPath(folder / entry.second), sizes.at(entry.first));
                });
            }, [this, id, number, origin, building, text, checked](std::expected<bool, StorageOutcome> holds)
            {
                if (holds && *holds)
                    checked->passed.push_back(text);
                if (--checked->pending == 0)
                    EndSeek(id, Sought{number, origin, checked->passed, building});
            });
        }
    });
}

void Engine::State::EndSeek(std::string const& id, Sought const& sought)
{
    auto found = torrents.find(id);
    if (found == torrents.end())
        return;
    auto& torrent = found->second;
    if (torrent.search == PassPhase::Again)
    {
        torrent.search = PassPhase::Wanted;
        return;
    }
    // A result that arrives after the question or the folder changed is
    // discarded; the change asks for its own search.
    if (torrent.facts.Asked().number != sought.number || !SameFolder(torrent.facts.savePath, sought.origin))
    {
        Settle(torrent, Searched{});
        return;
    }
    // While gozler still builds a volume's index, as after Windows starts, a
    // search that followed nothing asks again a minute later.
    if (sought.passed.empty() && sought.building)
    {
        torrent.search = PassPhase::Wanted;
        torrent.searchAt = std::chrono::steady_clock::now() + std::chrono::minutes(1);
        EndLookouts(id, std::nullopt);
        return;
    }
    // Two folders that hold the payload are two copies, and nothing shows
    // which one the person moved, so the person is asked.
    Searched searched;
    if (sought.passed.size() == 1)
        searched.folder = sought.passed.front();
    Settle(torrent, searched);
    if (torrent.waitingMove && torrent.waitingMove->cause == MoveCause::Follow)
        torrent.waitingMove->announced = true;
}
}
