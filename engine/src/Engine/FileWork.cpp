#include "Engine/State.h"
#include <algorithm>

namespace tt
{
std::vector<std::string> Engine::State::FileWork::Job::Folders() const
{
    std::vector<std::string> folders;
    for (auto const& use : uses)
    {
        if (!Contains(folders, use.folder))
            folders.push_back(use.folder);
    }
    return folders;
}

std::vector<std::string> Engine::State::FileWork::Job::Touched() const
{
    if (kind != WorkKind::Move || uses.empty())
        return Folders();
    std::vector<std::string> folders;
    for (auto const& use : uses)
    {
        if (use.torrentId == uses.front().torrentId && !Contains(folders, use.folder))
            folders.push_back(use.folder);
    }
    return folders;
}

void Engine::State::FileWork::Job::Queue()
{
    phase_ = JobPhase::Queued;
    unanswered_.clear();
    failure_.reset();
    Settle();
}

void Engine::State::FileWork::Job::Run()
{
    phase_ = JobPhase::Running;
    unanswered_.clear();
    failure_.reset();
}

void Engine::State::FileWork::Job::Await(std::string folder)
{
    phase_ = JobPhase::Unanswered;
    unanswered_ = std::move(folder);
    failure_.reset();
    Settle();
}

void Engine::State::FileWork::Job::Fail(Problem failure)
{
    phase_ = JobPhase::Failed;
    unanswered_.clear();
    failure_ = std::move(failure);
    Settle();
}

void Engine::State::FileWork::Job::Settle()
{
    worker.reset();
    progress.reset();
    pulse.reset();
}

std::shared_ptr<Engine::State::FileWork::Job> Engine::State::FileWork::Start(WorkKind kind, std::vector<Use> uses)
{
    std::erase_if(jobs_, [](auto const& entry) { return entry.expired(); });
    auto job = std::make_shared<Job>(kind, std::move(uses));
    jobs_.push_back(job);
    return job;
}

bool Engine::State::FileWork::Holds(std::function<bool(std::string const& folder)> const& on, bool names) const
{
    bool held = false;
    Each([&on, names, &held](Job const& job)
    {
        bool holds = job.kind == WorkKind::Deletion || (!names && job.kind == WorkKind::Move);
        held = held || (holds && std::any_of(job.uses.begin(), job.uses.end(),
            [&on](Use const& use) { return on(use.folder); }));
    });
    return held;
}

void Engine::State::FileWork::Opened(std::function<bool(std::string const& folder)> const& on)
{
    Each([&on](Job& job)
    {
        if (job.Phase() == JobPhase::Unanswered && on(job.Unanswered()))
            job.Queue();
    });
}

std::optional<std::string> Engine::State::Reach(std::vector<std::string> const& folders,
    std::function<void()> const& work)
{
    auto unanswered = [&folders]() -> std::optional<std::string>
    {
        for (auto const& folder : folders)
        {
            if (!Reachable(Wide(folder)))
                return folder;
        }
        return std::nullopt;
    };
    if (auto folder = unanswered())
        return folder;
    try
    {
        work();
    }
    catch (...)
    {
        if (auto folder = unanswered())
            return folder;
        throw;
    }
    return unanswered();
}
}
