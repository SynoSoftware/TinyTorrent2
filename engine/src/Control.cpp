#include "Torrent.h"
#include <algorithm>
#include <iterator>

namespace tt
{
namespace
{
// The torrent got further than when the engine continued it. Without both
// positions nothing shows that it did.
bool Further(std::optional<FileProgress> const& now, std::optional<FileProgress> const& then)
{
    return now && then && now->Beyond(*then);
}
}

bool FileProgress::Beyond(FileProgress const& earlier) const
{
    return done > earlier.done || uploaded > earlier.uploaded || checked > earlier.checked;
}

void Control::Ladder::Continue()
{
    level = stage == FailureStage::Stopped ? StorageRetry::Resumed : StorageRetry::Retried;
    stage = FailureStage::Continued;
}

void Control::Ladder::Stop(bool retryable)
{
    stage = !retryable || level == StorageRetry::Resumed ? FailureStage::Person : FailureStage::Stopped;
}

bool Control::Ladder::Waits() const
{
    return stage == FailureStage::Stopped || stage == FailureStage::Person;
}

void Control::Ladder::Return()
{
    stage = FailureStage::Recovering;
}

void Control::Ladder::LoseLocation()
{
    stage = FailureStage::Absent;
    level = StorageRetry::Automatic;
}

void Control::Ladder::FindLocation()
{
    stage = FailureStage::Recovering;
}

Control::Effects Control::Decide(Event const& event, Reality const& reality)
{
    auto before = *this;
    Effects effects;
    std::visit([&](auto const& happened) { On(happened, reality, effects); }, event);
    effects.changed = !(*this == before);
    return effects;
}

bool Control::Holds() const
{
    return (storage_ && storage_->ladder.stage != FailureStage::Continued) || Busy() || AwaitsReload();
}

bool Control::Busy() const
{
    return job_ && (job_->kind == JobKind::Release || job_->kind == JobKind::Reload);
}

bool Control::Recovers() const
{
    return (storage_ && (storage_->ladder.stage == FailureStage::Recovering ||
        storage_->ladder.stage == FailureStage::Absent)) || Busy();
}

bool Control::AwaitsReload() const
{
    return !lost_.empty();
}

std::optional<Problem> Control::Cause() const
{
    if (!storage_ || storage_->ladder.stage == FailureStage::Continued)
    {
        return std::nullopt;
    }
    return storage_->cause;
}

bool Control::NeedsPerson() const
{
    return StorageWaits() || (finalMove_ && finalMove_->Waits());
}

bool Control::StorageWaits() const
{
    return storage_ && storage_->ladder.Waits();
}

bool Control::AwaitsLocation() const
{
    return storage_ && storage_->ladder.stage == FailureStage::Absent;
}

bool Control::Failing() const
{
    return storage_ || finalMove_ || AwaitsReload();
}

bool Control::WantsProbe() const
{
    return storage_ && (storage_->ladder.stage == FailureStage::Recovering ||
        storage_->ladder.stage == FailureStage::Absent) && !job_;
}

bool Control::Owns(std::shared_ptr<Job const> const& job) const
{
    return job && (job == job_ || job == checkJob_);
}

void Control::On(Failed const& failed, Reality const& reality, Effects& effects)
{
    if (!storage_)
    {
        // The result of the release or reload that runs clears this error.
        if (Busy())
        {
            return;
        }
        storage_ = Failure{.cause = failed.cause, .retryable = failed.retryable};
    }
    else if (storage_->ladder.stage != FailureStage::Continued)
    {
        // The status reports the error each second; it is the same failure.
        storage_->cause = failed.cause;
        storage_->retryable = storage_->retryable && failed.retryable;
        return;
    }
    else if (Further(reality.progress, storage_->progress))
    {
        storage_ = Failure{.cause = failed.cause, .retryable = failed.retryable};
    }
    else
    {
        storage_->ladder.Return();
        storage_->cause = failed.cause;
        storage_->retryable = storage_->retryable && failed.retryable;
    }
    if (!Busy())
    {
        Start(job_, {.kind = JobKind::Release}, effects);
    }
}

void Control::On(Released const& released, Reality const& reality, Effects& effects)
{
    if (!End(job_, released.job, JobKind::Release))
    {
        return;
    }
    // The person answered while the release ran.
    if (!storage_)
    {
        effects.clear = true;
        return;
    }
    if (WantsProbe() && !reality.fileWork)
    {
        Start(job_, {.kind = JobKind::Probe}, effects);
    }
}

void Control::On(Probed const& probed, Reality const& reality, Effects& effects)
{
    if (!End(job_, probed.job, JobKind::Probe) || !probed.finding)
    {
        return;
    }
    auto usable = std::get_if<Usable>(&*probed.finding);
    if (usable)
    {
        Lose(usable->lost);
    }
    if (!WantsProbe())
    {
        return;
    }
    if (!usable)
    {
        storage_->ladder.LoseLocation();
        return;
    }
    storage_->ladder.FindLocation();
    Judge(reality, effects);
}

void Control::On(Checked const& checked, Reality const& reality, Effects& effects)
{
    if (End(checkJob_, checked.job, JobKind::FinalCheck))
    {
        if (checked.finding && std::holds_alternative<Usable>(*checked.finding) && finalMove_ &&
            finalMove_->stage == FailureStage::Stopped && !reality.fileWork)
        {
            finalMove_->Continue();
            effects.retryMove = true;
        }
        return;
    }
    if (!End(job_, checked.job, JobKind::Check) || !checked.finding)
    {
        return;
    }
    auto usable = std::get_if<Usable>(&*checked.finding);
    if (usable)
    {
        Lose(usable->lost);
    }
    if (!storage_ || storage_->ladder.stage != FailureStage::Stopped)
    {
        return;
    }
    if (std::holds_alternative<Absent>(*checked.finding))
    {
        // The location notice replaces the Retry button.
        storage_->ladder.LoseLocation();
        return;
    }
    if (usable && !reality.fileWork)
    {
        Continue(reality, effects);
    }
}

void Control::On(Reloaded const& reloaded, Reality const&, Effects& effects)
{
    if (!End(job_, reloaded.job, JobKind::Reload))
    {
        return;
    }
    if (!reloaded.failure)
    {
        lost_.clear();
    }
    if (!storage_)
    {
        // Files that lost their data still wait for this reload, so its
        // failure begins a failure of its own, which recovers as any other.
        if (reloaded.failure && !lost_.empty())
        {
            storage_ = Failure{.cause = *reloaded.failure};
            return;
        }
        effects.clear = true;
        return;
    }
    // A reload is a continue, also when it fails: its failure returned.
    storage_->ladder.Continue();
    if (reloaded.failure)
    {
        storage_->ladder.Stop(storage_->retryable);
        return;
    }
    // The reload changed the torrent's progress, so the first progress after
    // its resume-data check becomes the failure's.
    storage_->progress.reset();
    effects.clear = true;
}

void Control::On(Answered const& answered, Reality const&, Effects& effects)
{
    finalMove_.reset();
    if (answered.answer == FailureAnswer::Stop || answered.answer == FailureAnswer::Move)
    {
        return;
    }
    storage_.reset();
    // After any other answer than a removal the lost files still wait for
    // their reload, so they download again instead of the torrent writing
    // around zeros. A check waits for that reload too: a checkpoint saved
    // before the check would otherwise claim their data again.
    if (answered.answer == FailureAnswer::Remove)
    {
        lost_.clear();
    }
    // A release or reload that runs clears the error when it ends.
    effects.clear = !Busy();
}

void Control::On(Cleared const& cleared, Reality const&, Effects&)
{
    if (storage_ && storage_->cause == cleared.cause)
    {
        storage_.reset();
    }
}

void Control::On(Replaced const&, Reality const&, Effects&)
{
    storage_.reset();
    job_.reset();
    checkJob_.reset();
}

void Control::On(Dropped const&, Reality const&, Effects&)
{
    if (job_ && job_->kind != JobKind::Reload)
    {
        job_.reset();
    }
    checkJob_.reset();
}

void Control::On(Due const& due, Reality const& reality, Effects& effects)
{
    if (storage_ && storage_->ladder.stage == FailureStage::Continued)
    {
        if (!storage_->progress)
        {
            storage_->progress = reality.progress;
        }
        else if (Further(reality.progress, storage_->progress))
        {
            storage_.reset();
        }
    }
    else if (WantsProbe() && !reality.fileWork)
    {
        Start(job_, {.kind = JobKind::Probe}, effects);
    }
    else if (!storage_ && !lost_.empty() && !job_ && !reality.fileWork)
    {
        Start(job_, {.kind = JobKind::Reload}, effects);
    }
    if (!due.minute || reality.fileWork)
    {
        return;
    }
    // Each failure has its own check, so a final folder whose share hangs
    // never holds the check of the torrent's own folder.
    if (storage_ && storage_->ladder.stage == FailureStage::Stopped && !job_)
    {
        Start(job_, {.kind = JobKind::Check}, effects);
    }
    if (finalMove_ && finalMove_->stage == FailureStage::Stopped && !checkJob_)
    {
        Start(checkJob_, {.kind = JobKind::FinalCheck}, effects);
    }
}

void Control::On(MoveStarted const& started, Reality const&, Effects&)
{
    if (!started.final)
    {
        finalMove_.reset();
    }
}

void Control::On(MoveEnded const& ended, Reality const&, Effects&)
{
    if (!ended.failure || !ended.final)
    {
        finalMove_.reset();
        return;
    }
    // Its first failure enters the ladder at Stopped, so it waits for the
    // minute check.
    if (!finalMove_)
    {
        finalMove_ = Ladder{.stage = FailureStage::Stopped};
        return;
    }
    finalMove_->Stop(true);
}

bool Control::End(std::shared_ptr<Job>& slot, std::shared_ptr<Job const> const& job, JobKind kind)
{
    if (!job || job != slot || job->kind != kind)
    {
        return false;
    }
    slot.reset();
    return true;
}

void Control::Start(std::shared_ptr<Job>& slot, Job job, Effects& effects)
{
    slot = std::make_shared<Job>(std::move(job));
    effects.jobs.push_back(slot);
}

void Control::Lose(std::vector<lt::file_index_t> lost)
{
    // lost_ stays sorted and unique, whatever order the result found them in.
    std::sort(lost.begin(), lost.end());
    lost.erase(std::unique(lost.begin(), lost.end()), lost.end());
    std::vector<lt::file_index_t> merged;
    std::set_union(lost_.begin(), lost_.end(), lost.begin(), lost.end(), std::back_inserter(merged));
    lost_ = std::move(merged);
}

void Control::Judge(Reality const& reality, Effects& effects)
{
    // Windows reports an absent share with many codes, so only an answering
    // location shows that waiting cannot fix the error.
    if (!storage_->retryable || storage_->ladder.level != StorageRetry::Automatic)
    {
        storage_->ladder.Stop(storage_->retryable);
        return;
    }
    if (!reality.fileWork)
    {
        Continue(reality, effects);
    }
}

void Control::Continue(Reality const& reality, Effects& effects)
{
    if (reality.checking || !lost_.empty())
    {
        Start(job_, {.kind = JobKind::Reload}, effects);
        return;
    }
    storage_->ladder.Continue();
    storage_->progress = reality.progress;
    effects.clear = true;
}
}
