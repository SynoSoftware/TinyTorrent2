#include "SpeedHistory.h"

namespace tt
{
namespace
{
constexpr std::int64_t minuteLength = 60;
constexpr std::int64_t fiveMinutes = 5 * minuteLength;
constexpr std::int64_t oneDay = 24 * 60 * minuteLength;
// The seconds view keeps one sample per second. The minutes view keeps one
// average per finished minute; the minute in progress completes the day.
constexpr std::size_t secondSamples = fiveMinutes;
constexpr std::size_t minuteSamples = oneDay / minuteLength - 1;
// Samples come once a second, so a longer silence ends the minute in progress.
constexpr std::int64_t sampleGap = 2;
}

void SpeedHistory::Sample(std::int64_t time, double download, double upload)
{
    Point sample{time, download, upload};
    if (!seconds.empty() && time <= seconds.back().time)
    {
        seconds.clear();
        minutes.clear();
    }
    if (minuteCount && (time <= minute.time || time - minute.time > sampleGap))
    {
        minute = {};
        minuteCount = 0;
    }
    if (minuteCount && time / minuteLength != minute.time / minuteLength)
    {
        minutes.push_back({minute.time, minute.download / minuteCount, minute.upload / minuteCount});
        minute = {};
        minuteCount = 0;
    }
    minute.time = time;
    minute.download += sample.download;
    minute.upload += sample.upload;
    ++minuteCount;
    seconds.push_back(sample);
    while (!seconds.empty() && (seconds.size() > secondSamples || seconds.front().time <= time - fiveMinutes))
    {
        seconds.pop_front();
    }
    while (!minutes.empty() && (minutes.size() > minuteSamples || minutes.front().time <= time - oneDay))
    {
        minutes.pop_front();
    }
}

Json SpeedHistory::Read(bool day) const
{
    Json samples = Json::array();
    auto append = [&samples](Point const& sample)
    {
        samples.push_back({{"time", sample.time}, {"download_rate", sample.download},
            {"upload_rate", sample.upload}});
    };
    for (auto const& sample : day ? minutes : seconds)
    {
        append(sample);
    }
    if (day && minuteCount)
    {
        append({minute.time, minute.download / minuteCount, minute.upload / minuteCount});
    }
    return samples;
}
}
