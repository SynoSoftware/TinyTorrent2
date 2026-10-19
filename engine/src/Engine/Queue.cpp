#include "Engine/State.h"
#include <algorithm>

namespace tt
{
// libtorrent queues only torrents that still download; the others have
// position -1.
bool Engine::State::IsQueued(lt::queue_position_t position)
{
    return static_cast<int>(position) >= 0;
}

// libtorrent has applied the addition's files, or failed to, so it has queued
// it last or left it unqueued. The next status batch shows which.
void Engine::State::AwaitQueueStatus(Torrent& torrent)
{
    if (torrent.queueReport != QueueReport::AwaitingFiles)
    {
        return;
    }
    torrent.queueReport = QueueReport::AwaitingStatus;
    statusPending = true;
}

// The queued torrents in the accepted order. Status positions can still
// precede the last accepted queue move, so they place only the torrents that
// queueOrder does not list yet.
std::vector<std::string> Engine::State::CurrentQueue() const
{
    auto queued = [](Torrent const& torrent)
    {
        return !torrent.restore &&
            (torrent.queueReport != QueueReport::Reported || IsQueued(torrent.status.queue_position));
    };
    std::vector<std::string> order;
    std::set<std::string> listed;
    for (auto const& id : queueOrder)
    {
        auto found = torrents.find(id);
        if (found != torrents.end() && queued(found->second) && listed.insert(id).second)
        {
            order.push_back(id);
        }
    }
    std::vector<std::pair<int, std::string>> unlisted;
    for (auto const& [id, torrent] : torrents)
    {
        if (!listed.contains(id) && queued(torrent))
        {
            unlisted.emplace_back(static_cast<int>(torrent.status.queue_position), id);
        }
    }
    std::stable_sort(unlisted.begin(), unlisted.end());
    for (auto const& [position, id] : unlisted)
    {
        order.push_back(id);
    }
    return order;
}

void Engine::State::ApplyQueue()
{
    queuePending = false;
    std::erase_if(queueOrder, [this](auto const& id) { return !torrents.contains(id); });
    auto order = CurrentQueue();
    std::set<std::string> listed(queueOrder.begin(), queueOrder.end());
    for (auto const& id : order)
    {
        if (listed.insert(id).second)
        {
            queueOrder.push_back(id);
        }
    }
    int position = 0;
    for (auto const& id : order)
    {
        torrents.at(id).handle.queue_position_set(lt::queue_position_t(position++));
    }
}

void Engine::State::Queue(std::vector<std::string> const& ids, QueueMove move, std::string const& before,
    std::function<void(Outcome)> completion)
{
    auto order = CurrentQueue();
    for (auto const& id : ids)
    {
        if (!Contains(order, id))
        {
            completion({ErrorCode::InvalidTorrents});
            return;
        }
    }
    if (move == QueueMove::Before &&
        (!torrents.contains(before) || Contains(ids, before)))
    {
        completion({ErrorCode::InvalidTorrents});
        return;
    }
    order = Reorder(std::move(order), ids, move, before);
    auto document = Saved();
    document.queueOrder = order;
    changes.Commit([document = std::move(document)] { return document.ToJson(); },
        [this, order, completion](Outcome outcome)
    {
        if (!outcome.error)
        {
            queueOrder = order;
            ApplyQueue();
        }
        completion(std::move(outcome));
    });
}

// Moves the selected torrents and keeps their order among themselves.
std::vector<std::string> Engine::State::Reorder(std::vector<std::string> order,
    std::vector<std::string> const& ids, QueueMove move, std::string const& before)
{
    auto selected = [&ids](std::string const& id) { return Contains(ids, id); };
    if (move == QueueMove::Before)
    {
        std::vector<std::string> moving;
        for (auto const& id : order)
        {
            if (selected(id))
            {
                moving.push_back(id);
            }
        }
        std::erase_if(order, selected);
        order.insert(std::find(order.begin(), order.end(), before), moving.begin(), moving.end());
    }
    else if (move == QueueMove::Top || move == QueueMove::Bottom)
    {
        std::stable_partition(order.begin(), order.end(),
            [&selected, move](std::string const& id) { return selected(id) == (move == QueueMove::Top); });
    }
    else if (move == QueueMove::Up)
    {
        for (size_t index = 1; index < order.size(); ++index)
        {
            if (selected(order[index]) && !selected(order[index - 1]))
            {
                std::swap(order[index], order[index - 1]);
            }
        }
    }
    else
    {
        for (size_t index = order.size(); index > 1; --index)
        {
            if (selected(order[index - 2]) && !selected(order[index - 1]))
            {
                std::swap(order[index - 2], order[index - 1]);
            }
        }
    }
    return order;
}
}
