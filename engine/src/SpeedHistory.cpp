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

void SpeedHistory::Range::Sample(Point sample)
{
    if (count || !points.empty())
    {
        auto previous = count ? bucket.time : points.back().time;
        if (sample.time <= previous)
        {
            points.clear();
            bucket = {};
            count = 0;
        }
        else if (sample.time - previous > sampleGap)
        {
            Finish();
        }
    }
    if (count && sample.time / interval != bucket.time / interval)
    {
        Finish();
    }
    bucket.time = sample.time;
    bucket.download += sample.download;
    bucket.upload += sample.upload;
    ++count;
    Prune(sample.time);
}

void SpeedHistory::Range::Finish()
{
    if (!count)
    {
        return;
    }
    points.push_back(Average());
    bucket = {};
    count = 0;
}

SpeedHistory::Point SpeedHistory::Range::Average() const
{
    return {bucket.time, bucket.download / count, bucket.upload / count};
}

void SpeedHistory::Range::Prune(std::int64_t time)
{
    while (!points.empty() && (points.size() + (count ? 1 : 0) > capacity ||
        points.front().time <= time - duration))
    {
        points.pop_front();
    }
}

void SpeedHistory::Sample(std::int64_t time, double download, double upload)
{
    Point sample{time, download, upload};
    recent.Sample(sample);
    day.Sample(sample);
}

Json SpeedHistory::Read(bool day) const
{
    Json samples = Json::array();
    auto append = [&samples](Point const& sample)
    {
        samples.push_back({{"time", sample.time}, {"download_rate", sample.download},
            {"upload_rate", sample.upload}});
    };
    auto const& range = day ? this->day : recent;
    for (auto const& sample : range.points)
    {
        append(sample);
    }
    if (range.count)
    {
        append(range.Average());
    }
    return samples;
}
}
