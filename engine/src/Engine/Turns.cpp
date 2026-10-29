#include "Engine/State.h"
#include <Windows.h>
#include <algorithm>
#include <iterator>

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
        claims.drives.emplace(name, claim);
    for (auto const& root : drives.roots)
        claims.roots.emplace(root, claim);
}

Engine::State::Claim const* Engine::State::Blocked(Drives const& needed, Claims const& claims,
    std::vector<std::string> const& ids)
{
    auto blocking = [&ids](std::set<std::string> const& wanted, std::map<std::string, Claim> const& held)
        -> Claim const*
    {
        for (auto const& name : wanted)
        {
            auto found = held.find(name);
            if (found != held.end() && !(found->second.check && Contains(ids, found->second.torrentId)))
                return &found->second;
        }
        return nullptr;
    };
    if (auto claim = blocking(needed.names, claims.drives))
        return claim;
    return blocking(needed.roots, claims.roots);
}

void Engine::State::AddRoots(Drives& drives, std::vector<std::string> const& ids) const
{
    for (auto const& id : ids)
        drives.roots.insert(Working(id).storageRoot.key);
}

void Engine::State::StopStalledJobs()
{
    auto now = std::chrono::steady_clock::now();
    // A root whose probe has not returned may not answer, so a job that
    // touches it waits for the end of the Windows call, which decides by
    // Reach's rule. Interrupting it would make a root that does not answer
    // look like a failure of the work. Once the probe has waited
    // noticeDelay, the root's location notice counts the job's torrents.
    auto unanswered = [this](FileWork::Job const& job)
    {
        auto touched = job.Touched();
        return std::any_of(touched.begin(), touched.end(),
            [this](auto const& folder) { return ProbePending(folder); });
    };
    // Only a placement is timed, not the wait for libtorrent to release the
    // files. A move's copy that stalls is cancelled, and the move fails as
    // after any failed copy; a give-back that stalls fails as after any
    // error.
    fileWork.Each([now, &unanswered](FileWork::Job& job)
    {
        auto const& progress = job.progress;
        if (job.Phase() != JobPhase::Running || !progress || !progress->placing)
        {
            job.pulse.reset();
            return;
        }
        auto done = progress->done.load() + progress->compared.load();
        if (!job.pulse || done != job.pulse->done)
        {
            job.pulse = Pulse{done, now};
            return;
        }
        if (now - job.pulse->changed < stall || unanswered(job))
            return;
        // Undoing a copy gets its own `stall` before the next interruption,
        // so a slow deletion of the copies is not cut short, and an undo that
        // stalls too is interrupted again.
        job.pulse = Pulse{done, now};
        auto none = MoveCancel::None;
        progress->cancel.compare_exchange_strong(none, MoveCancel::Undo);
        // A cancel that Exit already made keeps the copies.
        if (progress->cancel == MoveCancel::Undo)
            job.worker->Interrupt();
    });
    // While a stalled check's reads remain, the other checks on its drive or
    // server cannot progress, so only the check that stalled first there gives
    // up.
    auto sessionPaused = Paused().has_value();
    auto stalled = StalledChecks();
    auto blocked = [&stalled](Torrent const& torrent) { return stalled.contains(torrent.storageRoot.key); };
    // A listed torrent whose check stalled stops with the drive's problem,
    // and the other checks on its root wait.
    auto stop = [this, &stalled](Torrent& torrent)
    {
        torrent.checkReads = CheckReads::Stalled;
        if (torrents.contains(torrent.torrentId))
        {
            Steer(torrent, Control::Failed{Problem{ProblemKind::DriveStalled, torrent.facts.savePath}});
            torrent.ApplyIntent();
        }
        log.Write("verify", torrent.torrentId, ToString(ProblemKind::DriveStalled));
        stalled.emplace(torrent.storageRoot.key, &torrent);
    };
    // The reads of a check that no longer runs, as after a stop or a removal,
    // still hold the hashing threads of its drive or server. They are timed
    // from when the check stopped running, before the running checks, so a
    // read that hangs names its own check, and the checks it holds up are
    // not blamed for it.
    EachWorking([this, now, sessionPaused, &blocked, &stop](Torrent& torrent)
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
        stop(torrent);
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
        stop(torrent);
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
        if (torrent.checkTurn && (stopped || !checking || failed || torrent.PathWork()))
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
            if (stalled.contains(torrent.storageRoot.key) ||
                (used && std::any_of(used->names.begin(), used->names.end(), [&claims](auto const& name)
                {
                    auto claim = claims.drives.find(name);
                    return claim != claims.drives.end() && !claim->second.check;
                })))
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
        // Its drives are kept from the later jobs of this pass.
        if (job.giveBack)
            continue;
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
    // A move keeps its drives until it ends, and a give-back while a try
    // runs. A job held inside a Windows call on a root that does not answer
    // touches no other drive until that call returns, so it keeps only the
    // drives of that root, and checks on its other drives can run.
    auto now = std::chrono::steady_clock::now();
    fileWork.Each([this, now, &claims](FileWork::Job const& job)
    {
        bool turns = job.kind == WorkKind::Move || job.kind == WorkKind::GiveBack;
        if (!turns || job.Phase() != JobPhase::Running || job.uses.empty())
            return;
        auto touched = job.Touched();
        std::vector<std::string> hung;
        std::copy_if(touched.begin(), touched.end(), std::back_inserter(hung),
            [this, now](auto const& folder) { return RootHangs(folder, now); });
        if (auto used = FindDrives(hung.empty() ? job.Folders() : hung))
            Reserve(claims, *used, {job.uses.front().torrentId});
    });
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
                auto behind = stalled.find(torrent.storageRoot.key);
                reads = behind == stalled.end() || behind->second == &torrent;
            }
            bool uses = running ? runs : !runs && reads;
            if (!uses)
                continue;
            if (auto used = FindDrives({torrent.facts.savePath}))
            {
                AddRoots(*used, {id});
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
        bool check = !job.destination && !job.giveBack;
        auto folders = job.giveBack ? job.giveBack->Folders() : Folders(job.ids, job.destination);
        auto needed = FindDrives(folders);
        // A check does not start behind a stalled check's reads on its drive
        // letter or server.
        bool behindStalled = check && std::any_of(job.ids.begin(), job.ids.end(),
            [this, &stalled](auto const& id) { return stalled.contains(Working(id).storageRoot.key); });
        if (needed && check)
            AddRoots(*needed, job.ids);
        if (!needed || Blocked(*needed, claims, job.ids) || behindStalled)
        {
            outcomes.push_back({ErrorCode::FilesBusy});
            continue;
        }
        auto ready = job.destination ? FilesReady(job.ids, *job.destination) : Outcome{};
        if (!ready.error)
            Reserve(claims, *needed, {job.ids.front(), check});
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
        // The drives are those of where the folder really is, so their
        // lookup waits for that.
        if (!Locate(folder))
        {
            known = false;
            continue;
        }
        auto real = RealPath(folder);
        auto [entry, added] = drives.try_emplace(real);
        if (added)
        {
            // Each root has its own worker, so a lookup that hangs on a share
            // that does not answer delays only the folders of that share.
            VolumeWorker(folder).Run([real] { return Drives::Of(Wide(real)); },
                [this, real](std::expected<Drives, StorageOutcome> result)
            {
                // A refused lookup runs again when a job next asks.
                if (result)
                    drives[real] = std::move(*result);
                else
                    drives.erase(real);
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
    // The file system's case rule, which PathBefore also compares by.
    auto name = root.wstring();
    if (name.empty())
        return {};
    auto size = static_cast<int>(name.size());
    std::wstring key(static_cast<std::size_t>(
        LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_UPPERCASE, name.c_str(), size, nullptr, 0, nullptr, nullptr, 0)),
        L'\0');
    LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_UPPERCASE, name.c_str(), size, key.data(),
        static_cast<int>(key.size()), nullptr, nullptr, 0);
    return Utf8(key);
}

Store& Engine::State::Worker(WorkerKind kind, std::string const& root)
{
    auto& worker = volumes[{kind, root}];
    if (!worker)
        worker = std::make_unique<Store>(wake);
    return *worker;
}

Store& Engine::State::VolumeWorker(std::string const& folder)
{
    return Worker(WorkerKind::Volume, RootOf(folder));
}

Store& Engine::State::AdditionWorker(std::string const& folder)
{
    return Worker(WorkerKind::Addition, RootOf(folder));
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
    fileWork.Each([this, &jobs](FileWork::Job const& work)
    {
        if (work.kind != WorkKind::GiveBack || work.turn == 0 || work.Phase() != JobPhase::Queued ||
            work.uses.empty())
            return;
        auto torrent = torrents.find(work.uses.front().torrentId);
        if (torrent == torrents.end() || !GiveBackReady(torrent->second))
            return;
        Job waiting{.turn = work.turn, .giveBack = &work};
        for (auto const& use : work.uses)
        {
            if (!Contains(waiting.ids, use.torrentId))
                waiting.ids.push_back(use.torrentId);
        }
        jobs.push_back(std::move(waiting));
    });
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

bool Engine::State::GetsDrives(FileWork::Job const& job)
{
    // The give-back gets its drives only when no job with an earlier turn
    // takes them, by the rule that shares them among all waiting jobs.
    auto jobs = WaitingJobs();
    auto claims = DrivesInUse();
    auto outcomes = ShareDrives(jobs, claims);
    for (std::size_t index = 0; index < jobs.size(); ++index)
    {
        if (jobs[index].giveBack == &job)
            return !outcomes[index].error;
    }
    return false;
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
    auto rootName = [this](std::string const& folder)
        { return Utf8(std::filesystem::path(Wide(RealPath(folder))).root_name().wstring()); };
    std::vector<std::string> ids{torrent.torrentId};
    std::optional<std::string> destination;
    if (status == Status::WaitingCheck)
    {
        auto stalled = StalledChecks();
        if (auto behind = stalled.find(torrent.storageRoot.key); behind != stalled.end())
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
        auto known = drives.find(RealPath(folder));
        if (known == drives.end() || !known->second)
            return {rootName(folder), {}};
        auto needed = *known->second;
        if (status == Status::WaitingCheck)
            AddRoots(needed, ids);
        if (auto claim = Blocked(needed, inUse, ids))
            return {rootName(folder), claim->torrentId};
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
    EachWorking([this, &stalled](Torrent const& torrent)
    {
        if (torrent.checkReads == CheckReads::Stalled)
            stalled.emplace(torrent.storageRoot.key, &torrent);
    });
    return stalled;
}
}
