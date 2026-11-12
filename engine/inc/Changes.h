#pragma once

#include "Engine.h"
#include "Log.h"
#include "Store.h"
#include <deque>
#include <filesystem>
#include <functional>

namespace tt
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
    // Commits `document`, which the storage worker encodes, so the engine
    // thread never serializes it. A change calls this at most once.
    template <typename Document>
    void Commit(Document document, std::function<void(Outcome)> completion)
    {
        Write([document = std::move(document)] { return document.ToJson(); }, std::move(completion));
    }
    bool IsIdle() const;

private:
    void Write(std::function<Json()> encode, std::function<void(Outcome)> completion);
    void Next();
    Store& store_;
    std::filesystem::path file_;
    Log& log_;
    std::deque<std::function<void()>> queue_;
    bool committing_ = false;
};
}
