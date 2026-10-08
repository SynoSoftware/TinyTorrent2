#pragma once

#include "Engine.h"
#include <deque>

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
    struct Point
    {
        std::int64_t time = 0;
        double download = 0;
        double upload = 0;
    };

    struct Range
    {
        std::int64_t duration;
        std::size_t capacity;
        int interval;
        std::deque<Point> points;
        Point bucket;
        int count = 0;

        void Configure(int value);
        void Sample(Point sample);
        void Finish();
        Point Average() const;
        void Prune(std::int64_t time);
    };

    Range recent{5 * 60, 5 * 60, 1};
    Range day{24 * 60 * 60, 24 * 60 * 60 / 10, 60};
};
}
