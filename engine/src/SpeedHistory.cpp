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
        auto previous = bucket.count ? bucket.time : points.back().time;
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
    points.push_back(Average());
    bucket = {};
}

SpeedHistory::Point SpeedHistory::Range::Average() const
{
    return {bucket.time, bucket.download / bucket.count, bucket.upload / bucket.count, bucket.start, bucket.count};
}

void SpeedHistory::Range::Prune(std::int64_t time)
{
    while (!points.empty() && (points.size() + (bucket.count ? 1 : 0) > capacity ||
        points.front().time <= time - duration))
    {
        points.pop_front();
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
            {"upload_rate", sample.upload}, {"start", sample.start}, {"count", sample.count}});
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
