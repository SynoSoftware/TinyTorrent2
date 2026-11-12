namespace Syno.TinyTorrent.Settings;

internal readonly record struct PeriodSpan
{
    /// <summary>
    /// Gestures and keys move periods and the time cursor in steps of this
    /// many minutes.
    /// </summary>
    internal const int Step = 15;

    /// <summary>The minute the day's last step starts, 23:45.</summary>
    internal const int LastStep = 1440 - Step;

    internal int Start { get; }
    internal int Duration { get; }
    internal int End => (Start + Duration) % 1440;

    internal PeriodSpan(int start, int duration)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(start, 1440);
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(duration, 1440);
        Start = start;
        Duration = duration;
    }

    internal static int Snap(double minute) =>
        (int)Math.Round(minute / Step, MidpointRounding.AwayFromZero) * Step;

    internal PeriodSpan Adjust(PeriodAction action, int delta)
    {
        var end = Start + Duration;
        switch (action)
        {
            case PeriodAction.Create:
                var boundary = Math.Clamp(Snap(Start + delta), 0, 1440);
                var from = Math.Min(Start, boundary);
                return new(Math.Min(from, LastStep), Math.Max(Step, Math.Abs(boundary - Start)));
            case PeriodAction.Move:
                return new(Math.Clamp(Snap(Start + delta), 0, LastStep), Duration);
            case PeriodAction.Start:
                var start = Math.Clamp(
                    Snap(Start + delta),
                    Math.Max(0, end - 1440),
                    Math.Min(1439, end - Math.Min(Step, Duration))
                );
                return new(start, end - start);
            case PeriodAction.End:
                return new(
                    Start,
                    Math.Clamp(Snap(end + delta) - Start, Math.Min(Step, Duration), 1440)
                );
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }
    }
}
