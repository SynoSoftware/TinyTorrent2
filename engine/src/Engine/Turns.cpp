#include "Engine/State.h"
#include <libtorrent/aux_/path.hpp>
#include <algorithm>
#include <cwctype>

// Which drives are in use is worked out again on every pass from the jobs
// that run, and never stored as a loan that a job must give back: a check or
// a move ends in many ways, and a forgotten return would block its drive
// until the engine restarts. The only stored fact is the order of the jobs
// that wait. A waiting job holds no drive, so a free drive goes to the first
// waiting job whose drives are all free.
namespace tt
{
namespace
{
// A running job whose progress does not change for this long gives up. A
// check of a 16 MiB piece over a 1 MB/s share still changes in 16 seconds.
constexpr auto stall = std::chrono::minutes(2);
}

bool Engine::State::Before(Job const& left, Job const& right)
{
    return left.forced != right.forced ? left.forced : left.turn < right.turn;
}

void Engine::State::Reserve(Claims& claims, Drives const& drives, Claim const& claim)
{
    for (auto const& name : drives.names)
        claims.emplace(name, claim);
}

std::optional<std::string> Engine::State::Blocked(Drives const& needed, Claims const& claims,
    std::vector<std::string> const& ids)
{
    for (auto const& name : needed.names)
    {
        auto found = claims.find(name);
        if (found != claims.end() && !(found->second.check && Contains(ids, found->second.torrentId)))
            return name;
    }
    return std::nullopt;
}

void Engine::State::AddRoots(Drives& drives, std::vector<std::string> const& folders)
{
    // The prefix keeps a root apart from the names Drives::Of gives.
    for (auto const& folder : folders)
        drives.names.insert("root " + RootOf(folder));
}

void Engine::State::StopStalledJobs()
{
    auto now = std::chrono::steady_clock::now();
    // Only the placement is timed, not the wait for libtorrent to release
    // the files. A copy that stalls is cancelled, and the move fails as after
    // any failed copy.
    for (auto& moving : moves)
    {
        auto const& progress = moving.progress;
        if (moving.phase != MovePhase::Copying || !progress || !progress->placing)
        {
            moving.pulse.reset();
            continue;
        }
        auto done = progress->done.load() + progress->compared.load();
        if (!moving.pulse || done != moving.pulse->done)
        {
            moving.pulse = Pulse{done, now};
        }
        else if (now - moving.pulse->changed >= stall)
        {
            // Undoing the copy gets its own 2 minutes before the next
            // interruption, so a slow deletion of the copies is not cut short,
            // and an undo that stalls too is interrupted again.
            moving.pulse = Pulse{done, now};
            auto none = MoveCancel::None;
            progress->cancel.compare_exchange_strong(none, MoveCancel::Undo);
            // A cancel that Exit already made keeps the copies.
            if (progress->cancel == MoveCancel::Undo)
                moving.worker->Interrupt();
        }
    }
    // An abandoned move that stalls fails, and its marker stays.
    for (auto& abandonment : abandonments)
    {
        if (!abandonment.worker)
            continue;
        auto done = abandonment.done->load();
        if (!abandonment.pulse || done != abandonment.pulse->done)
            abandonment.pulse = Pulse{done, now};
        else if (now - abandonment.pulse->changed >= stall)
        {
            abandonment.pulse = Pulse{done, now};
            abandonment.worker->Interrupt();
        }
    }
    // While a stalled check's reads remain, the other checks on its drive or
    // server cannot progress, so only the check that stalled first there gives
    // up.
    auto sessionPaused = Paused().has_value();
    auto stalled = StalledChecks();
    auto blocked = [&stalled](Torrent const& torrent) { return stalled.contains(RootOf(torrent.facts.savePath)); };
    // The reads of a check that no longer runs, as after a stop or a removal,
    // still hold the hashing threads of its drive or server. They are timed
    // from when the check stopped running, before the running checks, so a
    // read that hangs names its own check, and the checks it holds up are
    // not blamed for it.
    EachWorking([this, now, sessionPaused, &stalled, &blocked](Torrent& torrent)
    {
        bool runs = torrents.contains(torrent.torrentId) && torrent.ChecksFiles(sessionPaused);
        if (torrent.checkReads != CheckReads::Reading || runs)
        {
            torrent.readsSince.reset();
            return;
        }
        if (!torrent.readsSince)
            torrent.readsSince = now;
        if (blocked(torrent) || now - *torrent.readsSince < stall)
            return;
        torrent.readsSince.reset();
        torrent.checkReads = CheckReads::Stalled;
        if (torrents.contains(torrent.torrentId))
        {
            Steer(torrent, Control::Failed{Problem{ProblemKind::DriveStalled, torrent.facts.savePath}});
            torrent.ApplyIntent();
        }
        log.Write("verify", torrent.torrentId, ToString(ProblemKind::DriveStalled));
        stalled.emplace(RootOf(torrent.facts.savePath), &torrent);
    });
    for (auto& [id, torrent] : torrents)
    {
        if (!torrent.checkTurn || !torrent.checkTurn->running)
            continue;
        auto& pulse = *torrent.checkTurn->running;
        auto done = static_cast<std::uint64_t>(torrent.status.progress_ppm);
        // The clock runs only while the check runs.
        if (blocked(torrent) || !torrent.ChecksFiles(sessionPaused) || done != pulse.done)
        {
            pulse = {done, now};
            continue;
        }
        if (now - pulse.changed < stall)
            continue;
        torrent.checkTurn.reset();
        torrent.checkReads = CheckReads::Stalled;
        Steer(torrent, Control::Failed{Problem{ProblemKind::DriveStalled, torrent.facts.savePath}});
        torrent.ApplyIntent();
        log.Write("verify", id, ToString(ProblemKind::DriveStalled));
        stalled.emplace(RootOf(torrent.facts.savePath), &torrent);
    }
}

void Engine::State::AssignDrives()
{
    if (startup != Startup::Ready || IsShuttingDown())
        return;
    auto sessionPaused = Paused().has_value();
    // A check that libtorrent started since the last pass joins the waiting
    // jobs, last. A check keeps its place while Pause all lasts, and loses it
    // when the person stops it.
    std::vector<std::string> started;
    for (auto& [id, torrent] : torrents)
    {
        // A check continued at start keeps its saved turn while libtorrent
        // reads its resume data.
        bool checking = torrent.status.state == lt::torrent_status::checking_files ||
            torrent.status.state == lt::torrent_status::checking_resume_data;
        bool stopped = torrent.facts.intent == Intent::Paused;
        // libtorrent stops a check whose file fails with a torrent error.
        bool failed = torrent.control.Cause() || bool(torrent.status.errc);
        if (torrent.checkTurn && (stopped || !checking || failed || torrent.pathWork))
        {
            bool waited = torrent.CheckWaits();
            torrent.checkTurn.reset();
            if (waited)
                torrent.ApplyIntent();
        }
        if (!torrent.checkTurn && torrent.ChecksFiles(sessionPaused))
        {
            torrent.checkTurn = CheckTurn{++turns};
            started.push_back(id);
        }
    }
    // A running check waits again in its place when a move started on its
    // drive while Pause all stopped it, or while a stalled check's reads
    // remain on its drive letter or server, because no other check there can
    // progress then.
    if (!sessionPaused)
    {
        auto stalled = StalledChecks();
        auto claims = DrivesInUse();
        for (auto& [id, torrent] : torrents)
        {
            if (!torrent.checkTurn || !torrent.checkTurn->running)
                continue;
            auto used = FindDrives({torrent.facts.savePath});
            if (stalled.contains(RootOf(torrent.facts.savePath)) ||
                (used && std::any_of(used->names.begin(), used->names.end(), [&claims](auto const& name)
                { auto claim = claims.find(name); return claim != claims.end() && !claim->second.check; })))
            {
                torrent.checkTurn->running.reset();
                torrent.ApplyIntent();
            }
        }
    }
    auto jobs = WaitingJobs();
    // A move starts through the save queue, so while that queue is busy a
    // waiting move cannot start and claims no drive.
    if (!changes.IsIdle())
        std::erase_if(jobs, [](Job const& job) { return job.destination.has_value(); });
    auto claims = DrivesInUse();
    auto outcomes = ShareDrives(jobs, claims);
    auto now = std::chrono::steady_clock::now();
    bool released = false;
    for (std::size_t index = 0; index < jobs.size(); ++index)
    {
        auto const& job = jobs[index];
        auto const& outcome = outcomes[index];
        if (job.destination)
        {
            if (!outcome.error)
                StartWaitingMove(job.ids, *job.destination);
            // The rows show what the move waits for now, and a missing
            // metadata it no longer waits for would name the wrong cause.
            for (auto const& id : job.ids)
            {
                auto& torrent = torrents.at(id);
                if (outcome.error == ErrorCode::MetadataUnavailable)
                    RefuseMove(torrent, outcome);
                else if (torrent.waitingMove)
                    torrent.waitingMove->refusal.reset();
            }
            continue;
        }
        auto& torrent = torrents.at(job.ids.front());
        bool newcomer = Contains(started, torrent.torrentId);
        if (outcome.error)
        {
            if (newcomer)
                torrent.ApplyIntent();
            continue;
        }
        torrent.checkTurn->running = Pulse{static_cast<std::uint64_t>(torrent.status.progress_ppm), now};
        if (!newcomer)
        {
            torrent.ApplyIntent();
            released = true;
        }
    }
    // The released checks run from their next status, which this asks for at
    // once.
    if (released)
        RequestStatus();
}

Engine::State::Claims Engine::State::DrivesInUse()
{
    Claims claims;
    for (auto const& moving : moves)
    {
        // A move keeps its source drives until it ends, also while it deletes
        // the sources after its new folder is saved.
        auto folders = moving.sourceFolders;
        folders.push_back(moving.destination);
        auto holder = moving.ids.empty() ? std::string() : moving.ids.at(std::min(moving.current, moving.ids.size() - 1));
        if (auto used = FindDrives(folders))
            Reserve(claims, *used, {holder});
    }
    for (auto const& abandonment : abandonments)
    {
        auto torrent = FindWorking(abandonment.torrentId);
        if (!abandonment.worker || !torrent || !torrent->facts.marker)
            continue;
        if (auto used = FindDrives(Folders({abandonment.torrentId}, torrent->facts.marker->destination)))
            Reserve(claims, *used, {abandonment.torrentId});
    }
    // Pause all stops every check, so their drives are free while it lasts,
    // once libtorrent has no read left for them. The running checks claim
    // first, so a waiting row names the check that runs, not one that still
    // finishes its reads. The reads of other checks on a stalled check's drive
    // letter or server queue behind its reads and touch no drive until those
    // return, so they claim none.
    auto sessionPaused = Paused().has_value();
    auto stalled = StalledChecks();
    for (bool running : {true, false})
    {
        for (auto const& [id, torrent] : torrents)
        {
            bool runs = torrent.checkTurn && torrent.checkTurn->running && !sessionPaused;
            bool reads = torrent.checkReads != CheckReads::None;
            if (reads && !stalled.empty())
            {
                auto behind = stalled.find(RootOf(torrent.facts.savePath));
                reads = behind == stalled.end() || behind->second == &torrent;
            }
            bool uses = running ? runs : !runs && reads;
            if (!uses)
                continue;
            if (auto used = FindDrives({torrent.facts.savePath}))
            {
                AddRoots(*used, {torrent.facts.savePath});
                Reserve(claims, *used, {id, true});
            }
        }
    }
    return claims;
}

std::vector<Outcome> Engine::State::ShareDrives(std::vector<Job> const& jobs, Claims& claims)
{
    auto stalled = StalledChecks();
    std::vector<Outcome> outcomes;
    for (auto const& job : jobs)
    {
        auto folders = Folders(job.ids, job.destination);
        auto needed = FindDrives(folders);
        // A check does not start behind a stalled check's reads on its drive
        // letter or server.
        bool behindStalled = !job.destination && std::any_of(folders.begin(), folders.end(),
            [&stalled](auto const& folder) { return stalled.contains(RootOf(folder)); });
        if (needed && !job.destination)
            AddRoots(*needed, folders);
        if (!needed || Blocked(*needed, claims, job.ids) || behindStalled)
        {
            outcomes.push_back({ErrorCode::FilesBusy});
            continue;
        }
        auto ready = job.destination ? FilesReady(job.ids, *job.destination) : Outcome{};
        if (!ready.error)
            Reserve(claims, *needed, {job.ids.front(), !job.destination});
        outcomes.push_back(std::move(ready));
    }
    return outcomes;
}

std::optional<Drives> Engine::State::FindDrives(std::vector<std::string> const& folders)
{
    Drives found;
    bool known = true;
    for (auto const& folder : folders)
    {
        auto [entry, added] = drives.try_emplace(folder);
        if (added)
        {
            // Each root has its own worker, so a lookup that hangs on a share
            // that does not answer delays only the folders of that share.
            VolumeWorker(folder).Run([folder] { return Drives::Of(Wide(folder)); },
                [this, folder](std::expected<Drives, StorageOutcome> result)
            {
                // A refused lookup runs again when a job next asks.
                if (result)
                    drives[folder] = std::move(*result);
                else
                    drives.erase(folder);
            });
        }
        if (!entry->second)
        {
            known = false;
            continue;
        }
        found.names.insert(entry->second->names.begin(), entry->second->names.end());
    }
    if (!known)
        return std::nullopt;
    return found;
}

std::string Engine::State::RootKey(std::filesystem::path const& root)
{
    auto key = root.wstring();
    for (auto& letter : key)
        letter = static_cast<wchar_t>(std::towlower(letter));
    return Utf8(key);
}

std::string Engine::State::RootOf(std::string const& folder)
{
    return lt::storage_root_key(folder);
}

std::set<std::string> Engine::State::RootsOf(std::vector<std::filesystem::path> const& paths)
{
    std::set<std::string> roots;
    for (auto const& path : paths)
        roots.insert(RootOf(Utf8(path.wstring())));
    return roots;
}

Store& Engine::State::VolumeWorker(std::string const& folder)
{
    auto& worker = volumes[RootOf(folder)];
    if (!worker)
        worker = std::make_unique<Store>(wake);
    return *worker;
}

std::vector<std::string> Engine::State::Folders(std::vector<std::string> const& ids,
    std::optional<std::string> const& destination) const
{
    std::vector<std::string> folders;
    if (destination)
        folders.push_back(*destination);
    for (auto const& id : ids)
    {
        if (auto torrent = FindWorking(id))
            folders.push_back(torrent->facts.savePath);
    }
    return folders;
}

std::vector<Engine::State::Job> Engine::State::WaitingJobs() const
{
    // While Pause all lasts, a waiting check has nothing to start.
    auto sessionPaused = Paused().has_value();
    std::vector<Job> jobs;
    for (auto const& [id, torrent] : torrents)
    {
        if (torrent.CheckWaits() && !sessionPaused)
            jobs.push_back({{id}, std::nullopt, torrent.checkTurn->turn, torrent.facts.intent == Intent::Forced});
    }
    auto moving = WaitingMoves();
    // A move that waits for the give-back of its torrent's cut move takes no
    // turn and claims no drive until the give-back ends.
    for (auto& job : moving)
    {
        std::erase_if(job.ids, [this](auto const& id) { return Abandoning(id); });
    }
    std::erase_if(moving, [](Job const& job) { return job.ids.empty(); });
    jobs.insert(jobs.end(), std::make_move_iterator(moving.begin()), std::make_move_iterator(moving.end()));
    std::sort(jobs.begin(), jobs.end(), Before);
    return jobs;
}

std::vector<Engine::State::Job> Engine::State::WaitingMoves() const
{
    std::vector<Job> jobs;
    for (auto const& [id, torrent] : torrents)
    {
        if (!torrent.waitingMove)
            continue;
        auto turn = torrent.waitingMove->turn;
        auto group = std::find_if(jobs.begin(), jobs.end(), [turn](Job const& job) { return job.turn == turn; });
        if (group == jobs.end())
            jobs.push_back({{id}, torrent.waitingMove->destination, turn, false});
        else
            group->ids.push_back(id);
    }
    return jobs;
}

bool Engine::State::DrivesFree(std::vector<std::string> const& ids, std::string const& destination, bool waited)
{
    // A new move comes after the jobs that wait, as it would at the next
    // pass; a waiting move of its own torrents is the one it replaces. A move
    // that waited already had its turn, so only the jobs that run since then
    // can take its drives.
    auto jobs = waited ? std::vector<Job>{} : WaitingJobs();
    std::erase_if(jobs, [&ids](Job const& job)
        { return std::any_of(job.ids.begin(), job.ids.end(), [&ids](auto const& id) { return Contains(ids, id); }); });
    jobs.push_back({ids, destination});
    auto claims = DrivesInUse();
    return !ShareDrives(jobs, claims).back().error;
}

DriveWait Engine::State::WaitOf(Torrent const& torrent, Status status, Claims const& inUse) const
{
    auto rootName = [](std::string const& folder) { return Utf8(std::filesystem::path(Wide(folder)).root_name().wstring()); };
    std::vector<std::string> ids{torrent.torrentId};
    std::optional<std::string> destination;
    if (status == Status::WaitingCheck)
    {
        auto stalled = StalledChecks();
        if (auto behind = stalled.find(RootOf(torrent.facts.savePath)); behind != stalled.end())
            return {rootName(behind->second->facts.savePath), behind->second->torrentId};
    }
    else if (torrent.waitingMove)
    {
        for (auto& job : WaitingMoves())
        {
            if (job.turn == torrent.waitingMove->turn)
                ids = std::move(job.ids);
        }
        destination = torrent.waitingMove->destination;
    }
    else if (torrent.facts.marker)
    {
        destination = torrent.facts.marker->destination;
    }
    for (auto const& folder : Folders(ids, destination))
    {
        // The job waits until the lookup of its folder's drives returns.
        auto known = drives.find(folder);
        if (known == drives.end() || !known->second)
            return {rootName(folder), {}};
        auto needed = *known->second;
        if (status == Status::WaitingCheck)
            AddRoots(needed, {folder});
        if (auto name = Blocked(needed, inUse, ids))
            return {rootName(folder), inUse.at(*name).torrentId};
    }
    // A move whose drives are free can still wait for file work it reaches.
    if (destination)
    {
        if (auto ready = FilesReady(ids, *destination); ready.error == ErrorCode::FilesBusy)
            return {{}, {}, ready.detail};
    }
    return {};
}

std::map<std::string, Torrent const*> Engine::State::StalledChecks() const
{
    // A removed torrent's reads count until they return, because they hold
    // the hashing threads as a listed torrent's do.
    std::map<std::string, Torrent const*> stalled;
    EachWorking([&stalled](Torrent const& torrent)
    {
        if (torrent.checkReads == CheckReads::Stalled)
            stalled.emplace(RootOf(torrent.facts.savePath), &torrent);
    });
    return stalled;
}
}
