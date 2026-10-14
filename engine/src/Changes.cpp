#include "Changes.h"

namespace tt
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

void Changes::Commit(std::function<Json()> encode, std::function<void(Outcome)> completion)
{
    committing_ = true;
    store_.Write(file_, [encode = std::move(encode)] { return encode().dump(); },
        [this, completion = std::move(completion)](StorageOutcome stored)
    {
        auto outcome = stored.ToOutcome(ErrorCode::StorageFailed);
        if (outcome.error)
        {
            log_.Write("commit", "", ToString(*outcome.error));
        }
        completion(std::move(outcome));
        committing_ = false;
        Next();
    });
}

void Changes::Commit(std::function<Json()> encode, Reply reply, std::function<Json()> apply)
{
    Commit(std::move(encode),
        [reply = std::move(reply), apply = std::move(apply)](Outcome outcome)
    {
        reply(outcome.error ? Failure(*outcome.error, outcome.detail) : apply());
    });
}

bool Changes::IsIdle() const
{
    return !committing_ && queue_.empty();
}
}
