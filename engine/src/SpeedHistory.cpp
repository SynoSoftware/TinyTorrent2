#include "SpeedHistory.h"

namespace tt
{
namespace
{
// Engine ticks are one second apart; a longer silence must not join buckets.
constexpr std::int64_t sampleGap = 2;
}

void SpeedHistory::Configure(int recentInterval, int dayInterval)
{
    recent.Configure(recentInterval);
    day.Configure(dayInterval);
    // Recent zero runs then end where day samples end, which is where the
    // Speed view joins the two ranges.
    recent.span = dayInterval;
}

void SpeedHistory::Range::Configure(int value)
{
    if (interval == value)
    {
        return;
    }
    Finish();
    interval = value;
}

void SpeedHistory::Range::Sample(std::int64_t time, double download, double upload)
{
    if (bucket.count || !points.empty())
    {
        auto previous = bucket.count ? bucket.time : std::int64_t(points.back().time);
        if (time <= previous)
        {
            points.clear();
            bucket = {};
        }
        else if (time - previous > sampleGap)
        {
            Finish();
        }
    }
    if (bucket.count && time / interval != bucket.time / interval)
    {
        Finish();
    }
    if (!bucket.count)
        bucket.start = time;
    bucket.time = time;
    bucket.download += download;
    bucket.upload += upload;
    ++bucket.count;
    Prune(time);
}

void SpeedHistory::Range::Finish()
{
    if (!bucket.count)
    {
        return;
    }
    if (bucket.download == 0 && bucket.upload == 0 && !points.empty())
    {
        auto& last = points.back();
        auto start = std::int64_t(last.time) - last.length;
        if (last.download == 0 && last.upload == 0 && bucket.start - last.time <= sampleGap &&
            start / span == bucket.time / span)
        {
            last.time = std::uint32_t(bucket.time);
            last.length = std::uint16_t(bucket.time - start);
            last.count = std::uint16_t(last.count + bucket.count);
            bucket = {};
            return;
        }
    }
    points.push_back(Average());
    bucket = {};
}

SpeedHistory::Point SpeedHistory::Range::Average() const
{
    return {std::uint32_t(bucket.time), float(bucket.download / bucket.count), float(bucket.upload / bucket.count),
        std::uint16_t(bucket.time - bucket.start), std::uint16_t(bucket.count)};
}

void SpeedHistory::Range::Prune(std::int64_t time)
{
    std::size_t kept = points.size() + (bucket.count ? 1 : 0);
    auto first = points.begin();
    while (first != points.end() && (kept > capacity || first->time <= time - duration))
    {
        ++first;
        --kept;
    }
    points.erase(points.begin(), first);
    // Return the memory of a torrent whose activity stopped.
    if (points.capacity() > 2 * points.size())
    {
        points.shrink_to_fit();
    }
}

void SpeedHistory::Sample(std::int64_t time, double download, double upload)
{
    recent.Sample(time, download, upload);
    day.Sample(time, download, upload);
}

Json SpeedHistory::Read(bool day) const
{
    Json samples = Json::array();
    auto append = [&samples](Point const& sample)
    {
        samples.push_back({{"time", sample.time}, {"download_rate", sample.download},
            {"upload_rate", sample.upload}, {"start", std::int64_t(sample.time) - sample.length},
            {"count", sample.count}});
    };
    auto const& range = day ? this->day : recent;
    for (auto const& sample : range.points)
    {
        append(sample);
    }
    if (range.bucket.count)
    {
        append(range.Average());
    }
    return samples;
}
}
