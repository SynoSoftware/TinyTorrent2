// Feeds event sequences to Control::Decide and checks the stage, level and
// effects that the storage rules in engine.md predict. The pipe checks cannot
// order a share disconnect, a release and a probe result, and these sequences
// depend on that order.
#include "Torrent.h"
#include <algorithm>
#include <cstdio>
#include <functional>
#include <memory>
#include <optional>
#include <source_location>
#include <string>
#include <vector>

using namespace tt;

namespace
{
int failures = 0;
std::string current;

void Expect(bool holds, char const* what, std::source_location where = std::source_location::current())
{
    if (holds)
    {
        return;
    }
    ++failures;
    std::printf("FAIL %s: %s (line %u)\n", current.c_str(), what, static_cast<unsigned>(where.line()));
}

Problem const disk{ProblemKind::DiskFull};
Problem const absentShare{ProblemKind::DownloadPathUnavailable};
Problem const stalled{ProblemKind::DriveStalled};
Problem const names{ProblemKind::DownloadPathMissing};

FileProgress At(std::int64_t done)
{
    return {done, 0, 0};
}

std::vector<lt::file_index_t> const lostFiles{lt::file_index_t(2)};

// One torrent's control and what the engine knows of the torrent.
struct Subject
{
    Control control;
    Control::Reality reality{.progress = At(100)};
    Control::Effects last;

    Control::Effects const& Send(Control::Event const& event)
    {
        last = control.Decide(event, reality);
        return last;
    }
    std::shared_ptr<Control::Job const> Job() const
    {
        return control.Running();
    }
    void Fail(Problem const& cause = disk, bool retryable = true)
    {
        Send(Control::Failed{cause, retryable});
    }
    void Release()
    {
        Send(Control::Released{Job()});
    }
    void Probe(std::vector<lt::file_index_t> lost = {})
    {
        Send(Control::Probed{Job(), Control::Usable{std::move(lost)}});
    }
    void ProbeAbsent()
    {
        Send(Control::Probed{Job(), Control::Absent{}});
    }
    // The minute check of the torrent's own folder.
    void Check(Control::Finding finding)
    {
        Send(Control::Checked{Job(), std::move(finding)});
    }
    // The minute check of the failed move's final folder.
    void CheckFinal(Control::Finding finding)
    {
        Send(Control::Checked{control.Checking(), std::move(finding)});
    }
    void Reload(bool done)
    {
        Send(Control::Reloaded{Job(), done});
    }
    void Due(bool minute = false)
    {
        Send(Control::Due{minute});
    }
    // The engine's own move to the final folder fails with its files
    // complete where they were.
    void MoveFails()
    {
        Send(Control::MoveEnded{Problem{ProblemKind::MoveFailed}, true});
    }

    std::optional<FailureStage> Stage() const
    {
        return control.Storage() ? std::optional(control.Storage()->ladder.stage) : std::nullopt;
    }
    std::optional<StorageRetry> Level() const
    {
        return control.Storage() ? std::optional(control.Storage()->ladder.level) : std::nullopt;
    }
    std::optional<FailureStage> MoveStage() const
    {
        return control.FinalMove() ? std::optional(control.FinalMove()->stage) : std::nullopt;
    }
    bool Runs(JobKind kind) const
    {
        return Job() && Job()->kind == kind;
    }
    bool Started(JobKind kind) const
    {
        return std::any_of(last.jobs.begin(), last.jobs.end(), [kind](auto const& job) { return job->kind == kind; });
    }
};

// A failure, its release and a probe that answers: the continue at once.
void Retried(Subject& torrent, Problem const& cause = disk)
{
    torrent.Fail(cause);
    torrent.Release();
    torrent.Probe();
}

// The failure returned before the torrent got further and stopped it.
void Stopped(Subject& torrent, Problem const& cause = disk)
{
    Retried(torrent, cause);
    torrent.Fail(cause);
    torrent.Release();
    torrent.Probe();
}

// The engine's own move to the final folder failed and waits at Stopped.
void MoveStopped(Subject& torrent)
{
    torrent.Send(Control::MoveStarted{true});
    torrent.MoveFails();
}

struct Case
{
    char const* name;
    std::function<void()> run;
};

std::vector<Case> const cases{
    {"a failure continues once at once", []
    {
        Subject torrent;
        torrent.Fail();
        Expect(torrent.Runs(JobKind::Release) && torrent.control.Holds(), "a failure releases and holds");
        torrent.Release();
        Expect(torrent.Runs(JobKind::Probe), "the release starts a probe");
        torrent.Probe();
        Expect(torrent.last.clear, "the continue clears the error");
        Expect(torrent.Stage() == FailureStage::Continued && torrent.Level() == StorageRetry::Retried,
            "Continued at Retried");
        Expect(!torrent.control.Holds(), "the torrent runs");
    }},
    {"a failure that returns at once stops the torrent", []
    {
        Subject torrent;
        Stopped(torrent);
        Expect(torrent.Stage() == FailureStage::Stopped, "Stopped");
        Expect(torrent.control.Cause() == disk, "the cause is kept");
        Expect(torrent.control.Holds() && torrent.control.NeedsPerson(), "held, with Retry");
        Expect(!torrent.last.clear && !torrent.Job(), "no continue and no job");
    }},
    {"the minute check continues once, then the person", []
    {
        Subject torrent;
        Stopped(torrent);
        torrent.Due(true);
        Expect(torrent.Runs(JobKind::Check) && !torrent.control.Checking(), "the minute check of the folder");
        torrent.Check(Control::Usable{});
        Expect(torrent.last.clear && torrent.Stage() == FailureStage::Continued &&
            torrent.Level() == StorageRetry::Resumed, "the check continues at Resumed");
        torrent.Fail();
        torrent.Release();
        torrent.Probe();
        Expect(torrent.Stage() == FailureStage::Person && torrent.control.Holds(), "Person, held");
    }},
    {"a failure after other work cleared the stop starts its own ladder", []
    {
        Subject torrent;
        Stopped(torrent, names);
        torrent.Send(Control::Cleared{names});
        Expect(!torrent.Stage() && !torrent.control.Holds(), "the cleared stop ends");
        torrent.Fail(disk);
        Expect(torrent.Stage() == FailureStage::Recovering && torrent.Level() == StorageRetry::Automatic,
            "Recovering at Automatic");
        Expect(torrent.Started(JobKind::Release), "the release starts");
    }},
    {"a stop keeps the cause of a rename whose folder did not answer", []
    {
        Subject torrent;
        Stopped(torrent, absentShare);
        Expect(torrent.Stage() == FailureStage::Stopped && torrent.control.Cause() == absentShare,
            "Stopped with its cause");
        Expect(torrent.control.Holds(), "held");
    }},
    {"a location that does not answer keeps the failure at Automatic", []
    {
        Subject torrent;
        torrent.Fail();
        torrent.Release();
        torrent.ProbeAbsent();
        Expect(torrent.Stage() == FailureStage::Recovering && torrent.Level() == StorageRetry::Automatic,
            "Recovering at Automatic");
        Expect(torrent.control.AwaitsLocation() && torrent.control.Holds(), "absent, held");
    }},
    {"the location answers again and the torrent continues", []
    {
        Subject torrent;
        torrent.Fail();
        torrent.Release();
        torrent.ProbeAbsent();
        torrent.Due();
        Expect(torrent.Started(JobKind::Probe), "the next second probes again");
        torrent.Probe();
        Expect(torrent.last.clear && torrent.Level() == StorageRetry::Retried, "clear at Retried");
    }},
    {"a failed reload stops and waits for the minute check", []
    {
        Subject torrent;
        torrent.Fail();
        torrent.Release();
        torrent.Probe(lostFiles);
        Expect(torrent.Runs(JobKind::Reload) && torrent.Job()->lost == lostFiles, "lost files reload");
        torrent.Reload(false);
        Expect(torrent.Stage() == FailureStage::Stopped, "Stopped");
        torrent.Due();
        Expect(!torrent.Job(), "no reload before the minute check");
        torrent.Due(true);
        Expect(torrent.Runs(JobKind::Check), "the minute check");
    }},
    {"a reload whose add failed keeps the failure", []
    {
        Subject torrent;
        torrent.Fail();
        torrent.Release();
        torrent.Probe(lostFiles);
        torrent.Reload(false);
        Expect(torrent.Stage() == FailureStage::Stopped && torrent.control.Cause() == disk,
            "Stopped, the failure kept");
    }},
    {"Retry during a release clears once the release ends", []
    {
        Subject torrent;
        torrent.Fail();
        torrent.Send(Control::Answered{});
        Expect(!torrent.Stage() && !torrent.last.clear && torrent.control.Busy(), "the release still runs");
        torrent.Release();
        Expect(torrent.last.clear && !torrent.Job() && !torrent.Stage(), "clear, no probe, no failure");
    }},
    {"an answer during a probe leaves no stop", []
    {
        Subject torrent;
        torrent.Fail();
        torrent.Release();
        torrent.Send(Control::Answered{});
        torrent.reality.retryable = false;
        torrent.Probe();
        Expect(!torrent.Stage() && !torrent.control.NeedsPerson(), "no failure and no stop");
        Expect(torrent.last.jobs.empty(), "no reload");
    }},
    {"a move's new folder ends the failure, and the late probe changes nothing", []
    {
        Subject torrent;
        torrent.Fail();
        torrent.Release();
        auto probe = torrent.Job();
        torrent.Send(Control::Replaced{});
        Expect(!torrent.Stage() && !torrent.Job(), "the failure ends");
        torrent.Send(Control::Probed{probe, Control::Usable{}});
        Expect(!torrent.last.changed && !torrent.last.clear && torrent.last.jobs.empty(),
            "the late probe changes nothing");
    }},
    {"Verify while a reload runs clears once the reload ends", []
    {
        Subject torrent;
        torrent.Fail();
        torrent.Release();
        torrent.Probe(lostFiles);
        torrent.Send(Control::Answered{});
        Expect(torrent.control.Busy() && torrent.control.Holds() && !torrent.last.clear, "busy until the result");
        Expect(torrent.control.Lost() == lostFiles, "checkpoints still leave out the lost files");
        torrent.Reload(true);
        Expect(torrent.last.clear && !torrent.control.Busy() && !torrent.Stage(), "then clear");
    }},
    {"a stalled check reloads from its resume data", []
    {
        Subject torrent;
        torrent.reality.checking = true;
        torrent.Fail(stalled);
        torrent.Release();
        torrent.Probe();
        Expect(torrent.Runs(JobKind::Reload) && torrent.Job()->lost.empty(), "reload from the resume data");
        torrent.Reload(true);
        Expect(torrent.Stage() == FailureStage::Continued && torrent.last.clear, "Continued");
    }},
    {"a second error during a release starts no second release", []
    {
        Subject torrent;
        torrent.Fail(disk);
        auto release = torrent.Job();
        torrent.Fail(absentShare);
        Expect(torrent.control.Cause() == absentShare, "the cause is updated");
        Expect(torrent.last.jobs.empty() && torrent.Job() == release, "no second release");
    }},
    {"the final-folder move continues once after the minute check, then the person", []
    {
        Subject torrent;
        MoveStopped(torrent);
        Expect(torrent.MoveStage() == FailureStage::Stopped && torrent.control.NeedsPerson(), "Stopped");
        torrent.Due(true);
        Expect(torrent.control.Checking() && torrent.control.Checking()->kind == JobKind::FinalCheck &&
            !torrent.Job(), "the minute check of the final folder");
        torrent.CheckFinal(Control::Usable{});
        Expect(torrent.last.retryMove && torrent.control.FinalMove()->level == StorageRetry::Resumed,
            "the move runs again at Resumed");
        torrent.Send(Control::MoveStarted{true});
        torrent.MoveFails();
        Expect(torrent.MoveStage() == FailureStage::Person, "Person");
    }},
    {"Start at Person ends the failure", []
    {
        Subject torrent;
        Stopped(torrent);
        torrent.Due(true);
        torrent.Check(Control::Usable{});
        torrent.Fail();
        torrent.Release();
        torrent.Probe();
        torrent.Send(Control::Answered{});
        Expect(torrent.last.clear && !torrent.Stage() && !torrent.control.Holds(), "clear, no failure");
    }},
    {"another move replaces the failed final-folder move", []
    {
        Subject torrent;
        MoveStopped(torrent);
        torrent.Send(Control::MoveStarted{false});
        Expect(!torrent.control.FinalMove() && !torrent.control.NeedsPerson(), "no final-folder failure");
        torrent.Send(Control::MoveEnded{Problem{ProblemKind::MoveFailed}, false});
        Expect(!torrent.control.FinalMove(), "the other move's failure is the person's");
        torrent.Due(true);
        Expect(!torrent.Job() && !torrent.control.Checking(), "no minute check");
    }},
    {"the person's move that waits replaces the failed final-folder move", []
    {
        Subject torrent;
        MoveStopped(torrent);
        torrent.Send(Control::Answered{.storage = false});
        Expect(!torrent.control.FinalMove() && !torrent.control.NeedsPerson(), "no Retry without an error");
        torrent.Due(true);
        Expect(!torrent.Job() && !torrent.control.Checking(), "no minute check");
    }},
    {"a stopped torrent whose location goes recovers at Automatic", []
    {
        Subject torrent;
        Stopped(torrent);
        torrent.Due(true);
        torrent.Check(Control::Absent{});
        Expect(torrent.Stage() == FailureStage::Recovering && torrent.Level() == StorageRetry::Automatic,
            "Recovering at Automatic");
        Expect(torrent.control.AwaitsLocation() && !torrent.control.NeedsPerson(), "absent, no Retry");
    }},
    {"the first progress after a reload becomes the failure's", []
    {
        Subject torrent;
        torrent.Fail();
        torrent.Release();
        torrent.Probe(lostFiles);
        torrent.reality.progress.reset();
        torrent.Reload(true);
        torrent.Due();
        Expect(!torrent.control.Storage()->progress, "no progress while the resume data is checked");
        torrent.reality.progress = At(40);
        torrent.Due();
        Expect(torrent.control.Storage()->progress == At(40), "the first progress is recorded");
        torrent.Fail();
        torrent.Release();
        torrent.Probe(lostFiles);
        Expect(torrent.Stage() == FailureStage::Stopped && !torrent.Job(), "no second reload");
    }},
    {"the same error each second changes nothing at Stopped", []
    {
        Subject torrent;
        Stopped(torrent);
        for (int second = 0; second < 5; ++second)
        {
            torrent.Fail();
        }
        Expect(torrent.Stage() == FailureStage::Stopped && !torrent.Job(), "Stopped, no job");
    }},
    {"each stopped failure gets its own minute check", []
    {
        Subject torrent;
        Stopped(torrent);
        MoveStopped(torrent);
        torrent.Due(true);
        Expect(torrent.Runs(JobKind::Check) && torrent.control.Checking() && torrent.last.jobs.size() == 2,
            "both checks");
    }},
    {"an error while a job without a failure runs opens no failure", []
    {
        Subject torrent;
        torrent.Fail();
        torrent.Send(Control::Answered{});
        torrent.Fail();
        Expect(!torrent.Stage(), "the release's error opens no failure");
        torrent.Release();
        Expect(torrent.last.clear && !torrent.Stage() && !torrent.Job(), "clear, no failure, no probe");
    }},
    {"a release without a failure moves nothing", []
    {
        // The engine releases a healthy torrent's files without an event, as
        // after a dropped pause alert. Nothing in the ladder moves.
        Subject torrent;
        torrent.Send(Control::Released{std::make_shared<Control::Job>()});
        torrent.Due(true);
        Expect(!torrent.last.changed && !torrent.Stage() && !torrent.Job() && !torrent.control.Holds(),
            "no failure, no job, no hold");
    }},
    {"a seed limit's stop answers nothing", []
    {
        // The engine's own stop sends no Answered, so the failed move keeps
        // its stop and its minute check.
        Subject torrent;
        MoveStopped(torrent);
        torrent.Due(true);
        Expect(torrent.MoveStage() == FailureStage::Stopped && torrent.control.Checking(),
            "the move stays Stopped");
    }},
    {"a dropped handle ends the probe and the minute checks on it", []
    {
        Subject torrent;
        torrent.Fail();
        torrent.Release();
        auto probe = torrent.Job();
        torrent.Send(Control::Dropped{});
        Expect(!torrent.Job() && torrent.Stage() == FailureStage::Recovering, "the probe ends, the failure stays");
        torrent.Due();
        Expect(torrent.Started(JobKind::Probe), "the next second probes again");
        torrent.Send(Control::Probed{probe, Control::Usable{}});
        Expect(!torrent.last.changed, "the old probe's result changes nothing");
        Subject stopped;
        Stopped(stopped);
        MoveStopped(stopped);
        stopped.Due(true);
        stopped.Send(Control::Dropped{});
        Expect(!stopped.Job() && !stopped.control.Checking(), "the minute checks end");
        stopped.Due(true);
        Expect(stopped.Started(JobKind::Check) && stopped.Started(JobKind::FinalCheck), "the next minute checks again");
    }},
    {"a dropped handle leaves the reload that removed it", []
    {
        Subject torrent;
        torrent.Fail();
        torrent.Release();
        torrent.Probe(lostFiles);
        torrent.Send(Control::Dropped{});
        Expect(torrent.Runs(JobKind::Reload), "the reload still runs");
        torrent.Reload(true);
        Expect(torrent.Stage() == FailureStage::Continued, "and ends with its own result");
    }},
    {"Retry during a reload that fails clears the error", []
    {
        Subject torrent;
        torrent.Fail();
        torrent.Release();
        torrent.Probe(lostFiles);
        torrent.Send(Control::Answered{});
        torrent.Reload(false);
        Expect(torrent.last.clear && !torrent.Job() && !torrent.Stage(), "clear, no failure");
    }},
    {"lost files stay with the failure until a reload drops them", []
    {
        Subject torrent;
        torrent.Fail();
        torrent.Release();
        torrent.Probe(lostFiles);
        torrent.Reload(false);
        Expect(torrent.control.Lost() == lostFiles, "a failed reload keeps them");
        torrent.Due(true);
        torrent.Check(Control::Usable{lostFiles});
        Expect(torrent.Runs(JobKind::Reload) && torrent.control.Lost() == lostFiles, "the next reload drops them");
        torrent.Reload(true);
        Expect(torrent.control.Lost().empty(), "a reload that succeeds ends them");
    }},
    {"a rename error that waiting cannot fix shows Retry once the folder answers", []
    {
        // The final name already exists, which no wait fixes.
        Problem const exists{ProblemKind::TorrentError, "Cannot create a file when that file already exists."};
        Subject torrent;
        torrent.Fail(exists, false);
        torrent.Release();
        torrent.Probe();
        Expect(torrent.Stage() == FailureStage::Person && torrent.control.Cause() == exists,
            "Person, with the rename's error");
        torrent.Due(true);
        Expect(!torrent.Job(), "no rename runs again by itself");
    }},
    {"a final folder that does not answer keeps the move stopped", []
    {
        Subject torrent;
        MoveStopped(torrent);
        torrent.Due(true);
        torrent.CheckFinal(Control::Absent{});
        Expect(!torrent.last.retryMove && torrent.MoveStage() == FailureStage::Stopped, "no move, Stopped");
        torrent.Due(true);
        Expect(torrent.Started(JobKind::FinalCheck), "the next minute checks again");
    }},
    {"the folder's check ends apart from the final folder's", []
    {
        Subject torrent;
        Stopped(torrent);
        MoveStopped(torrent);
        torrent.Due(true);
        auto final = torrent.control.Checking();
        torrent.Check(Control::Absent{});
        Expect(torrent.Stage() == FailureStage::Recovering && !torrent.Job() && torrent.control.Checking() == final,
            "the folder's finding applies while the final folder's check runs");
        torrent.Due();
        Expect(torrent.Started(JobKind::Probe), "the next second probes");
        torrent.Send(Control::Checked{final, Control::Usable{}});
        Expect(torrent.last.retryMove && !torrent.control.Checking(), "the final folder's check still counts");
    }},
    {"a final folder whose share hangs leaves the folder's minute check", []
    {
        Subject torrent;
        Stopped(torrent);
        MoveStopped(torrent);
        torrent.Due(true);
        auto hangs = torrent.control.Checking();
        torrent.Check(Control::Unusable{});
        Expect(torrent.Stage() == FailureStage::Stopped && !torrent.Job() && torrent.control.Checking() == hangs,
            "the folder's check ends, the final folder's still runs");
        torrent.Due(true);
        Expect(torrent.Started(JobKind::Check) && !torrent.Started(JobKind::FinalCheck) &&
            torrent.control.Checking() == hangs, "the next minute checks the folder again");
        torrent.Check(Control::Usable{});
        Expect(torrent.last.clear && torrent.Stage() == FailureStage::Continued, "the folder continues");
        Expect(torrent.MoveStage() == FailureStage::Stopped, "the move stays Stopped");
    }},
};

// The invariant run: every sequence of up to five events from a fixed set.
struct State
{
    Control control;
    Control::Reality reality{.progress = At(100)};
    // The jobs before the current ones, whose results are late.
    std::shared_ptr<Control::Job const> previous;
    std::shared_ptr<Control::Job const> previousCheck;
    // Continues since the failure began or its level was reset.
    int continues = 0;
};

using Variant = std::function<std::optional<Control::Event>(State&)>;

Control::Event Probed(std::shared_ptr<Control::Job const> job, std::vector<lt::file_index_t> lost = {})
{
    return Control::Probed{std::move(job), Control::Usable{std::move(lost)}};
}

Control::Event Checked(std::shared_ptr<Control::Job const> job, Control::Finding finding)
{
    return Control::Checked{std::move(job), std::move(finding)};
}

std::vector<Variant> const variants{
    [](State&) { return Control::Event{Control::Failed{disk, true}}; },
    [](State&) { return Control::Event{Control::Failed{names, false}}; },
    [](State& state) { return Control::Event{Control::Released{state.control.Running()}}; },
    [](State& state) { return Probed(state.control.Running()); },
    [](State& state) { return Probed(state.control.Running(), lostFiles); },
    [](State& state) { return Control::Event{Control::Probed{state.control.Running(), Control::Absent{}}}; },
    [](State& state) { return Control::Event{Control::Probed{state.control.Running()}}; },
    [](State& state) { return Checked(state.control.Running(), Control::Usable{}); },
    [](State& state) { return Checked(state.control.Running(), Control::Absent{}); },
    [](State& state) { return Checked(state.control.Checking(), Control::Usable{}); },
    [](State& state) { return Checked(state.control.Checking(), Control::Absent{}); },
    [](State& state) { return Control::Event{Control::Reloaded{state.control.Running(), true}}; },
    [](State& state) { return Control::Event{Control::Reloaded{state.control.Running(), false}}; },
    [](State&) { return Control::Event{Control::Answered{}}; },
    [](State&) { return Control::Event{Control::Answered{.storage = false}}; },
    [](State&) { return Control::Event{Control::Cleared{disk}}; },
    [](State&) { return Control::Event{Control::Replaced{}}; },
    [](State&) { return Control::Event{Control::Dropped{}}; },
    [](State&) { return Control::Event{Control::Due{false}}; },
    [](State&) { return Control::Event{Control::Due{true}}; },
    [](State&) { return Control::Event{Control::MoveEnded{Problem{ProblemKind::MoveFailed}, true}}; },
    [](State&) { return Control::Event{Control::MoveStarted{false}}; },
    // Late results of the job before the current one.
    [](State& state) { return Control::Event{Control::Released{state.previous}}; },
    [](State& state) { return Probed(state.previous); },
    [](State& state) { return Checked(state.previous, Control::Usable{}); },
    [](State& state) { return Checked(state.previousCheck, Control::Usable{}); },
    [](State& state) { return Control::Event{Control::Reloaded{state.previous, true}}; },
    // Changes of what the engine knows, which are no events.
    [](State& state) -> std::optional<Control::Event>
    {
        state.reality.progress->done += 10;
        return std::nullopt;
    },
    [](State& state) -> std::optional<Control::Event>
    {
        state.reality.fileWork = !state.reality.fileWork;
        return std::nullopt;
    },
    [](State& state) -> std::optional<Control::Event>
    {
        state.reality.checking = !state.reality.checking;
        return std::nullopt;
    },
};

std::shared_ptr<Control::Job const> JobOf(Control::Event const& event)
{
    if (auto released = std::get_if<Control::Released>(&event))
    {
        return released->job;
    }
    if (auto probed = std::get_if<Control::Probed>(&event))
    {
        return probed->job;
    }
    if (auto checked = std::get_if<Control::Checked>(&event))
    {
        return checked->job;
    }
    if (auto reloaded = std::get_if<Control::Reloaded>(&event))
    {
        return reloaded->job;
    }
    return nullptr;
}

std::uint64_t decisions = 0;

void Verify(State const& before, State& after, Control::Event const& event, Control::Effects const& effects)
{
    auto const& was = before.control.Storage();
    auto const& is = after.control.Storage();
    // Invariant 2: a stop has its cause.
    if (is && is->ladder.Waits())
    {
        Expect(after.control.Cause().has_value(), "a stop has its cause");
    }
    // Invariant 5: a result whose job is gone changes nothing.
    bool result = std::holds_alternative<Control::Released>(event) || std::holds_alternative<Control::Probed>(event) ||
        std::holds_alternative<Control::Checked>(event) || std::holds_alternative<Control::Reloaded>(event);
    if (result && !before.control.Owns(JobOf(event)))
    {
        Expect(after.control == before.control && effects.jobs.empty() && !effects.clear && !effects.retryMove,
            "a late result changes nothing");
    }
    // Invariant 5: each failure has at most one job, the storage failure's in
    // its slot and the final-folder check in the other.
    auto running = after.control.Running();
    auto checking = after.control.Checking();
    Expect(!running || running->kind != JobKind::FinalCheck, "the storage failure runs no final-folder check");
    Expect(!checking || checking->kind == JobKind::FinalCheck, "the final-folder slot holds only its check");
    for (auto const& job : effects.jobs)
    {
        Expect(job == running || job == checking, "a started job is its failure's job");
    }
    // Invariant 3: only the end of a failure, or a location that does not
    // answer, lowers the level.
    bool restarted = !was || (std::holds_alternative<Control::Failed>(event) &&
        was->ladder.stage == FailureStage::Continued && was->progress && before.reality.progress &&
        before.reality.progress->Beyond(*was->progress));
    if (was && is && is->ladder.level < was->ladder.level)
    {
        Expect(restarted || is->absent, "the level lowers only when the failure ends or the location is absent");
    }
    // Invariant 4: at most two continues per failure; a failed reload counts
    // as one whose failure returned.
    if (!is || restarted ||
        (is->ladder.level == StorageRetry::Automatic && was && was->ladder.level != StorageRetry::Automatic))
    {
        after.continues = 0;
    }
    bool reloads = std::any_of(effects.jobs.begin(), effects.jobs.end(),
        [](auto const& job) { return job->kind == JobKind::Reload; });
    bool continues = effects.clear && is && is->ladder.stage == FailureStage::Continued && was &&
        was->ladder.stage != FailureStage::Continued && !std::holds_alternative<Control::Reloaded>(event);
    if (is && (reloads || continues))
    {
        ++after.continues;
    }
    Expect(after.continues <= 2, "at most two continues per failure");
    // A failure that waits for the engine has its job, or Due starts one once
    // the torrent's files are free, whatever the other failure's job does.
    bool storageWaits =
        is && (is->ladder.stage == FailureStage::Recovering || is->ladder.stage == FailureStage::Stopped);
    bool moveWaits = after.control.FinalMove() && after.control.FinalMove()->stage == FailureStage::Stopped;
    if ((storageWaits && !running) || (moveWaits && !checking))
    {
        auto due = after.control;
        auto reality = after.reality;
        reality.fileWork = false;
        due.Decide(Control::Due{true}, reality);
        Expect((!storageWaits || due.Running()) && (!moveWaits || due.Checking()),
            "a failure that waits for the engine gets a job");
    }
}

void Explore(State const& state, int depth)
{
    if (depth == 0)
    {
        return;
    }
    for (auto const& variant : variants)
    {
        auto next = state;
        auto event = variant(next);
        if (event)
        {
            auto effects = next.control.Decide(*event, next.reality);
            ++decisions;
            if (next.control.Running() != state.control.Running() && state.control.Running())
            {
                next.previous = state.control.Running();
            }
            if (next.control.Checking() != state.control.Checking() && state.control.Checking())
            {
                next.previousCheck = state.control.Checking();
            }
            Verify(state, next, *event, effects);
            if (failures > 20)
            {
                return;
            }
        }
        Explore(next, depth - 1);
    }
}
}

int main()
{
    for (auto const& entry : cases)
    {
        current = entry.name;
        entry.run();
    }
    current = "invariant run";
    // Invariant 7 holds by type: Effects has no member that resumes a
    // torrent.
    Explore({}, 5);
    std::printf("%zu cases, %llu decisions, %d failures\n", cases.size(),
        static_cast<unsigned long long>(decisions), failures);
    return failures == 0 ? 0 : 1;
}
