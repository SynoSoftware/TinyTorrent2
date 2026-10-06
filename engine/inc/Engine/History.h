#pragma once

#include "Engine.h"
#include <deque>

namespace tt
{
class SpeedHistory
{
public:
    void Sample(std::int64_t time, double download, double upload);
    Json Read(bool day) const;

private:
    struct Point
    {
        std::int64_t time = 0;
        double download = 0;
        double upload = 0;
    };

    std::deque<Point> seconds;
    std::deque<Point> minutes;
    Point minute;
    int minuteCount = 0;
};
}
