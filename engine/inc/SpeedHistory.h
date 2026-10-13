#pragma once

#include "Engine.h"
#include <vector>

namespace tt
{
class SpeedHistory
{
public:
    // Intervals are validated engine settings in seconds.
    void Configure(int recentInterval, int dayInterval);
    void Sample(std::int64_t time, double download, double upload);
    Json Read(bool day) const;

private:
    // Every torrent keeps up to a day of these, so they stay small: times are
    // Unix seconds, which fit 32 bits until 2106, and a point covers at most
    // an hour of samples.
    struct Point
    {
        std::uint32_t time = 0;
        float download = 0;
        float upload = 0;
        // Seconds from the first sample to time, and the number of samples.
        std::uint16_t length = 0;
        std::uint16_t count = 0;
    };

    struct Bucket
    {
        std::int64_t time = 0;
        double download = 0;
        double upload = 0;
        std::int64_t start = 0;
        int count = 0;
    };

    struct Range
    {
        std::int64_t duration;
        std::size_t capacity;
        int interval;
        // Zero buckets join one point within each aligned span of seconds, so
        // a stopped or idle torrent keeps a few points instead of thousands.
        int span;
        // A vector, not a deque: MSVC's deque allocates every point on its
        // own, which costs more than moving a few kilobytes when the oldest
        // point leaves.
        std::vector<Point> points;
        Bucket bucket;

        void Configure(int value);
        void Sample(std::int64_t time, double download, double upload);
        void Finish();
        Point Average() const;
        void Prune(std::int64_t time);
    };

    Range recent{5 * 60, 5 * 60, 1, 60};
    Range day{24 * 60 * 60, 24 * 60 * 60 / 10, 60, 60 * 60};
};
}
