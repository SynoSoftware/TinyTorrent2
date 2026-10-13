#pragma once

#include "Engine.h"
#include <condition_variable>
#include <deque>
#include <filesystem>
#include <functional>
#include <mutex>
#include <string>
#include <thread>

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
    void Write(std::filesystem::path path, std::function<std::string()> encode,
        std::function<void(StorageOutcome)> completion);
    void Drain();
    bool IsIdle() const;
    void Abandon();
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
