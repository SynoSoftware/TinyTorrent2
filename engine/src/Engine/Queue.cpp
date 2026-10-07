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

std::vector<std::string> Engine::State::CurrentQueue() const
{
    std::vector<std::pair<int, std::string>> positions;
    for (auto const& [id, torrent] : torrents)
    {
        if (torrent.restore)
        {
            continue;
        }
        auto position = torrent.handle.queue_position();
        if (!torrent.deleted && IsQueued(position))
        {
            positions.emplace_back(static_cast<int>(position), id);
        }
    }
    std::stable_sort(positions.begin(), positions.end());
    std::vector<std::string> order;
    for (auto const& [position, id] : positions)
    {
        order.push_back(id);
    }
    return order;
}

void Engine::State::ApplyQueue()
{
    std::erase_if(queueOrder, [this](auto const& id) { return !torrents.contains(id) || torrents.at(id).deleted; });
    for (auto const& id : CurrentQueue())
    {
        if (!Contains(queueOrder, id))
        {
            queueOrder.push_back(id);
        }
    }
    int position = 0;
    for (auto const& id : queueOrder)
    {
        if (!torrents.at(id).restore && IsQueued(torrents.at(id).handle.queue_position()))
        {
            torrents.at(id).handle.queue_position_set(lt::queue_position_t(position++));
        }
    }
}

void Engine::State::Queue(std::vector<std::string> const& ids, QueueMove move, std::string const& before,
    Reply reply)
{
    for (auto const& id : ids)
    {
        if (torrents.at(id).restore || !IsQueued(torrents.at(id).handle.queue_position()))
        {
            reply(Failure(ErrorCode::InvalidTargets));
            return;
        }
    }
    bool unknownTarget = !before.empty() && (!torrents.contains(before) || torrents.at(before).deleted);
    if (move == QueueMove::Before && (unknownTarget || Contains(ids, before)))
    {
        reply(Failure(ErrorCode::InvalidTargets));
        return;
    }
    auto order = Reorder(CurrentQueue(), ids, move, before);
    auto document = Saved();
    document.queueOrder = order;
    changes.Commit(document.ToJson(), reply, [this, order]
    {
        queueOrder = order;
        ApplyQueue();
        return Success();
    });
}

// Moves the selected torrents and keeps their order among themselves. An
// empty `before` moves them to the end.
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
