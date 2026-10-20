#pragma once

#include "Engine.h"
#include <condition_variable>
#include <deque>
#include <expected>
#include <filesystem>
#include <functional>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <thread>
#include <type_traits>

namespace tt
{
struct StorageOutcome
{
    std::optional<StorageFailure> failure;
    std::string detail;

    // The engine's outcome: Overloaded when the store refused the work,
    // otherwise `code` with the cause of a failure.
    Outcome ToOutcome(ErrorCode code) const;
};

class Store
{
public:
    explicit Store(std::function<void()> wake);
    ~Store();
    void Run(std::function<void()> work, std::function<void(StorageOutcome)> completion);
    // Hands the value `work` returns to `completion`, or the failure when the
    // store refused the work or the work threw.
    template <typename Work, typename Completion>
        requires (!std::is_void_v<std::invoke_result_t<Work&>>)
    void Run(Work work, Completion completion)
    {
        using Value = std::invoke_result_t<Work&>;
        using Result = std::expected<Value, StorageOutcome>;
        auto value = std::make_shared<std::optional<Value>>();
        Run([work = std::move(work), value]() mutable { value->emplace(work()); },
            [completion = std::move(completion), value](StorageOutcome outcome) mutable
        {
            if (outcome.failure)
                completion(Result(std::unexpect, std::move(outcome)));
            else
                completion(Result(std::in_place, std::move(**value)));
        });
    }
    void Write(std::filesystem::path path, std::function<std::string()> encode,
        std::function<void(StorageOutcome)> completion);
    void Drain();
    bool IsIdle() const;
    void Abandon();
    // Cancels the Windows call that the job in progress waits in, so a job
    // stuck on a drive that stopped responding fails and its completion runs.
    void Interrupt();
    // Drops queued work and stops waiting for the job in progress, which
    // waits until the process ends, such as a parked copy. Its completion
    // never runs, and the destructor returns at once.
    void Detach();
    static std::string Read(std::filesystem::path const& path);

private:
    struct Job
    {
        std::function<void()> work;
        std::function<void(StorageOutcome)> completion;
    };
    struct Completed
    {
        std::function<void(StorageOutcome)> completion;
        StorageOutcome outcome;
    };
    mutable std::mutex mutex_;
    std::condition_variable ready_;
    std::condition_variable idle_;
    std::deque<Job> jobs_;
    std::deque<Completed> completed_;
    bool stopping_ = false;
    bool working_ = false;
    std::function<void()> wake_;
    std::thread worker_;
};
}
