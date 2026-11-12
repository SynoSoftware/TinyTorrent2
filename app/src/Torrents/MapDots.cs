using System.Diagnostics;
using System.Numerics;

namespace Syno.TinyTorrent.Torrents;

/// <summary>
/// The moving dots that show transfer speed on the peer map. One dot stands
/// for the same number of bytes on every route, and each route launches its
/// dots evenly at its measured rate. The dot size keeps the number in flight
/// near a target that grows with the total rate against the connection
/// capacity, up to a maximum that follows the canvas area.
/// </summary>
/// <typeparam name="TRoute">The drawing's route, which a dot follows.</typeparam>
/// <param name="smallest">The smallest dot size in bytes, a power of two.</param>
internal sealed class MapDots<TRoute>(long smallest)
    where TRoute : notnull
{
    // These values are judgement, checked on the running map.
    private const double Speed = 160;
    private const double ShortestTrip = .5;
    private const double LongestTrip = 3;
    internal const double ShortRoute = 20;
    private const double AreaPerDot = 10_000;
    private const double Spacing = 15;

    // Even departures add up to one dot to each route, so the size aims below
    // the target and the maximum rarely drops a dot.
    private const double Margin = .8;

    // A route keeps its lane while it carries dots, so its bytes carry over.
    private readonly Dictionary<TRoute, Lane> _lanes = [];
    // Where the next new lane starts within its first dot, as a share of the dot's bytes.
    // Steady traffic leaves every route somewhere within its next dot. Lanes that all started
    // at zero waited a whole dot each, so after a new subject the first dot left only when the
    // busiest route alone had carried one, about 8 s. Steps of the golden ratio spread the
    // starts evenly however many lanes begin together.
    private double _phase;
    private const double PhaseStep = .618033988749895;
    private readonly List<Dot> _flying = [];
    private int _maximum = 1;
    private double _area;
    private double _speed = 1;
    private double _total = 1;
    private long? _target;
    private long? _lowerSince;
    private long _counted;

    /// <summary>
    /// The bytes one dot stands for, or null before the first traffic. The
    /// size rises at once but falls only after ten seconds of lower traffic,
    /// so the legend does not flicker; a new area, speed or total, and the
    /// first traffic after <see cref="Clear"/>, apply at once.
    /// </summary>
    internal long? Bytes { get; private set; }

    /// <summary>The total rate of the routes that carry dots, in bytes per second.</summary>
    internal double Total { get; private set; }

    internal IReadOnlyList<Dot> Flying => _flying;

    /// <summary>
    /// Counts the bytes until <paramref name="now"/> at the previous rates,
    /// then takes the new rates. Routes left out lose their dots.
    /// </summary>
    internal void Update(Input input, long now)
    {
        Advance(now);
        List<Lane>? started = null;
        foreach (var traffic in input.Routes.Values)
        {
            if (!_lanes.TryGetValue(traffic.Route, out var lane))
            {
                _lanes[traffic.Route] = lane = new Lane();
                (started ??= []).Add(lane);
            }
            lane.Down = traffic.Down;
            lane.Up = traffic.Up;
            lane.Trip = traffic.Trip;
        }
        foreach (var route in _lanes.Keys)
            if (!input.Routes.ContainsKey(route))
                _lanes.Remove(route);
        _flying.RemoveAll(dot => !input.Routes.ContainsKey(dot.Route));
        Total = input.Rate;
        var reshaped = _target is null || input.Area != _area || input.Speed != _speed || input.Total != _total;
        _area = input.Area;
        _speed = input.Speed;
        _total = input.Total;
        _maximum = input.Maximum;
        // Without traffic the size stays, so the legend keeps the last one.
        if (Total > 0)
            _target = Fit(input.Needed);
        Settle(now, reshaped);
        // After Settle, so a new subject's lanes start within its own dot size.
        if (started is not null && Bytes is { } size)
        {
            foreach (var lane in started)
            {
                _phase = (_phase + PhaseStep) % 1;
                lane.Downloaded = _phase * size;
                _phase = (_phase + PhaseStep) % 1;
                lane.Uploaded = _phase * size;
            }
        }
    }

    /// <summary>
    /// Ends traffic accounting while keeping the last dot size for the legend
    /// until the next traffic takes its own.
    /// </summary>
    internal void Clear()
    {
        _flying.Clear();
        _lanes.Clear();
        _target = null;
        _lowerSince = null;
        Total = 0;
    }

    /// <summary>
    /// Lands the dots whose trip is over and launches a dot for every
    /// <see cref="Bytes"/> each route completed since the last call.
    /// </summary>
    internal void Advance(long now)
    {
        if (_lowerSince is { } since)
        {
            var deadline = since + 10 * Stopwatch.Frequency;
            if (_counted < deadline && deadline < now)
                Advance(deadline);
        }
        var seconds = (now - _counted) / (double)Stopwatch.Frequency;
        _counted = now;
        _flying.RemoveAll(dot => dot.Progress(now) >= 1);
        if (Bytes is not { } size)
            return;
        foreach (var (route, lane) in _lanes)
        {
            lane.Downloaded = Launch(route, lane, false, lane.Down, lane.Downloaded);
            lane.Uploaded = Launch(route, lane, true, lane.Up, lane.Uploaded);
        }
        Settle(now, false);

        double Launch(TRoute route, Lane lane, bool upload, double rate, double carried)
        {
            if (rate <= 0)
                return carried;
            carried += rate * seconds;
            var due = Math.Floor(carried / size);
            carried -= due * size;
            // A dot leaves when its bytes are complete, which can be before
            // now: the newest left carried ÷ rate seconds ago, and each older
            // one a dot's bytes earlier. One whose trip is already over is not
            // shown late, and none goes past the maximum.
            for (var back = 0; back < due && _flying.Count < _maximum; back++)
            {
                var start = now - (long)((carried + back * size) / rate * Stopwatch.Frequency);
                var dot = new Dot(route, upload, start, lane.Trip);
                if (dot.Progress(now) >= 1)
                    break;
                _flying.Add(dot);
            }
            return carried;
        }
    }

    /// <summary>The next departure or size change; hidden routes need only the size deadline.</summary>
    internal TimeSpan? Wait(bool departures = true)
    {
        if (Bytes is not { } size)
            return null;
        var seconds = _lowerSince is { } since
            ? Math.Max(0, (since + 10 * Stopwatch.Frequency - _counted) / (double)Stopwatch.Frequency)
            : double.PositiveInfinity;
        if (departures)
        {
            foreach (var lane in _lanes.Values)
            {
                if (lane.Down > 0)
                    seconds = Math.Min(seconds, (size - lane.Downloaded) / lane.Down);
                if (lane.Up > 0)
                    seconds = Math.Min(seconds, (size - lane.Uploaded) / lane.Up);
            }
        }
        return double.IsFinite(seconds) ? TimeSpan.FromSeconds(Math.Max(0, seconds)) : null;
    }

    // The smallest size in the series 1, 1.5, 2, 3, 4, 6 … × smallest that
    // keeps the mean dots in flight, rate × trip ÷ size over all routes,
    // within the target, and the dots of each direction of each route at
    // least Spacing apart. Steps of ×1.5 and ×1.33 keep the count within a
    // third of the target; powers of two would let a 4% rise in speed halve it.
    private long Fit(double needed)
    {
        var size = smallest;
        while (size < needed)
            size = BitOperations.IsPow2(size) ? size / 2 * 3 : size / 3 * 4;
        return size;
    }

    private void Settle(long now, bool reshaped)
    {
        if (_target is not { } target || target == Bytes)
        {
            _lowerSince = null;
            return;
        }
        if (Bytes is not { } bytes || target > bytes || reshaped)
        {
            Bytes = target;
            _lowerSince = null;
        }
        else if (_lowerSince is not { } since)
            _lowerSince = now;
        else if (now - since >= 10 * Stopwatch.Frequency)
        {
            Bytes = target;
            _lowerSince = null;
        }
    }

    /// <summary>A route's rates in bytes per second and its length in DIPs on the fitted overview.</summary>
    internal readonly record struct Traffic(TRoute Route, double Down, double Up, double Length)
    {
        internal double Trip { get; init; }
    }

    internal sealed class Input
    {
        internal Input(IEnumerable<Traffic> routes, double area, double ceiling, double speed, double total)
        {
            Dictionary<TRoute, Traffic> rates = new(routes.TryGetNonEnumeratedCount(out var count) ? count : 0);
            foreach (var traffic in routes)
            {
                // A dot on a short route would never leave the fades at its ends.
                if (traffic.Length >= ShortRoute)
                    rates[traffic.Route] = traffic with
                    {
                        Trip = Math.Clamp(traffic.Length / MapDots<TRoute>.Speed, ShortestTrip, LongestTrip) / speed,
                    };
            }
            Routes = rates;
            Area = area;
            Speed = speed;
            Total = total;
            var rate = 0.0;
            var inFlight = 0.0;
            foreach (var traffic in rates.Values)
            {
                rate += traffic.Down + traffic.Up;
                inFlight += (traffic.Down + traffic.Up) * traffic.Trip;
            }
            Rate = rate;
            Maximum = Math.Max(1, (int)(area * total / AreaPerDot));
            // People judge how crowded the view looks rather than count dots, and
            // the cube root spreads the count evenly from a trickle to capacity.
            var wanted = Math.Max(1, Maximum * Math.Min(1, Math.Cbrt(Rate / ceiling)));
            var needed = inFlight / (Margin * wanted);
            foreach (var traffic in rates.Values)
                needed = Math.Max(needed, Math.Max(traffic.Down, traffic.Up) * traffic.Trip * Spacing / traffic.Length);
            Needed = needed;
        }

        internal IReadOnlyDictionary<TRoute, Traffic> Routes { get; }
        internal double Area { get; }
        internal double Speed { get; }
        internal double Total { get; }
        internal double Rate { get; }
        internal int Maximum { get; }
        internal double Needed { get; }
    }

    /// <summary>A dot in flight: one <see cref="Bytes"/> of a route's download or upload.</summary>
    /// <param name="Trip">The seconds its route takes from end to end.</param>
    internal readonly record struct Dot(TRoute Route, bool Upload, long Start, double Trip)
    {
        /// <summary>How far the dot has travelled, from 0 at its start to 1 at its end.</summary>
        internal double Progress(long now) => (now - Start) / (double)Stopwatch.Frequency / Trip;
    }

    /// <summary>A route's rates, trip and the bytes it carries toward its next dots.</summary>
    private sealed class Lane
    {
        internal double Down { get; set; }
        internal double Up { get; set; }
        internal double Trip { get; set; }
        internal double Downloaded { get; set; }
        internal double Uploaded { get; set; }
    }
}
