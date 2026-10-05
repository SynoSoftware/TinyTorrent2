#pragma once

#include "Engine.h"
#include "Log.h"
#include "Store.h"
#include <deque>
#include <filesystem>
#include <functional>

namespace tiny
{
// Edits to the saved document run one at a time, in order. A change that
// commits holds the queue until its write completes, so the next change
// edits a document that includes it.
class Changes
{
public:
    Changes(Store& store, std::filesystem::path file, Log& log);
    // Returns false, and queues nothing, when the queue is full.
    bool Queue(std::function<void()> change);
    // Writes the document; a change calls this at most once.
    void Commit(Json document, std::function<void(StorageOutcome)> completion);
    // Commits a command's change. Only after the write succeeds does `apply`
    // change the state to match; its result is the reply.
    void Commit(Json document, Reply reply, std::function<Json()> apply);
    bool IsIdle() const;

private:
    void Next();
    Store& store_;
    std::filesystem::path file_;
    Log& log_;
    std::deque<std::function<void()>> queue_;
    bool committing_ = false;
};
}
