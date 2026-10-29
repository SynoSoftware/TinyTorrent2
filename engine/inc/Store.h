#pragma once

#include "Engine.h"
#include <libtorrent/error_code.hpp>
#include <condition_variable>
#include <deque>
#include <exception>
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
    libtorrent::error_code cause;

    // The engine's outcome: Overloaded when the store refused the work,
    // otherwise `code` with the cause of a failure.
    Outcome ToOutcome(ErrorCode code) const;
};

class Store
{
public:
    // The largest file Store reads and writes unless its caller allows more.
    // Writes keep to the limit their reads use, so every file written can be
    // read back.
    static constexpr std::size_t fileLimit = 16 * 1024 * 1024;

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
        std::function<void(StorageOutcome)> completion, std::size_t limit = fileLimit);
    // Runs the completions of finished work. A completion that throws goes to
    // `failed`, and the others still run.
    void Drain(std::function<void(std::exception const&)> const& failed);
    bool IsIdle() const;
    void Abandon();
    // Cancels the Windows call that the job in progress waits in, so a job
    // stuck on a drive that stopped responding fails and its completion runs.
    void Interrupt();
    // Drops queued work and stops waiting for the job in progress, which
    // waits until the process ends, such as a parked copy. Its completion
    // never runs, and the destructor returns at once.
    void Detach();
    static std::string Read(std::filesystem::path const& path, std::size_t limit = fileLimit);

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
