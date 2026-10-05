#include "Changes.h"

namespace tiny
{
namespace
{
// Bounds the queue, so that a burst of commands cannot grow memory without
// limit.
constexpr std::size_t queueLimit = 64;
}

Changes::Changes(Store& store, std::filesystem::path file, Log& log) :
    store_(store), file_(std::move(file)), log_(log) {}

bool Changes::Queue(std::function<void()> change)
{
    if (queue_.size() >= queueLimit)
    {
        return false;
    }
    queue_.push_back(std::move(change));
    Next();
    return true;
}

void Changes::Next()
{
    while (!committing_ && !queue_.empty())
    {
        auto change = std::move(queue_.front());
        queue_.pop_front();
        change();
    }
}

void Changes::Commit(Json document, std::function<void(StorageOutcome)> completion)
{
    committing_ = true;
    store_.Write(file_, [document = std::move(document)] { return document.dump(); },
        [this, completion = std::move(completion)](StorageOutcome outcome)
    {
        if (!outcome.succeeded)
        {
            log_.Write("commit", "", "storage_failed");
        }
        completion(std::move(outcome));
        committing_ = false;
        Next();
    });
}

void Changes::Commit(Json document, Reply reply, std::function<Json()> apply)
{
    Commit(std::move(document),
        [reply = std::move(reply), apply = std::move(apply)](StorageOutcome outcome)
    {
        reply(outcome.succeeded ? apply() : Failure("storage_failed", outcome.detail));
    });
}

bool Changes::IsIdle() const
{
    return !committing_ && queue_.empty();
}
}
