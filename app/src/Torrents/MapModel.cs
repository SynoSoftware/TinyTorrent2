using System.ComponentModel;
using System.Numerics;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shell;

namespace Syno.TinyTorrent.Torrents;

internal sealed class MapModel(Inspector owner) : INotifyPropertyChanged
{
    private Observation[] _observations = [];
    internal object Subject => owner.PeerSubject;
    internal Dictionary<string, Group> Countries { get; private set; } = [];
    internal List<Destination> Destinations { get; private set; } = [];
    internal Location? Origin { get; private set; }
    internal GeoPoint? OriginPoint { get; private set; }

    /// <summary>The longitude at the map's centre: the origin's, or 0° while it is unknown.</summary>
    internal float Meridian => (float)(OriginPoint?.Longitude ?? 0);

    internal Strings? Text { get; private set; }
    internal bool Cities { get; private set; }
    internal PeerPlace? Place => owner.Place;
    public event PropertyChangedEventHandler? PropertyChanged;

    // The model outlives the map's unload and reload, so the peak lasts the
    // engine session.
    private double _peak;
    private double? _capacity;

    /// <summary>
    /// The rate in bytes per second that the moving dots measure traffic
    /// against: the saved connection capacity of both directions, or else the
    /// highest total seen in this engine session but at least 1 MiB/s, so a
    /// trickle on a fresh session does not look like a full connection. A total
    /// above a stale capacity replaces it.
    /// </summary>
    internal double Ceiling => Math.Max(_capacity ?? 1024 * 1024, _peak);

    /// <summary>Records the total rate the map's dots show, in bytes per second.</summary>
    internal void RaisePeak(double total) => _peak = Math.Max(_peak, total);

    /// <summary>Forgets the peak, as when the engine session changes.</summary>
    internal void ResetPeak() => _peak = 0;

    internal void RefreshPlace() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Place)));

    /// <param name="capacity">The connection capacity, download plus upload, in bytes per second, or null when unknown.</param>
    internal void Update(
        IReadOnlyList<Peer>? peers,
        Location? origin,
        Strings text,
        bool cities = true,
        double? capacity = null
    )
    {
        peers ??= [];
        _observations = peers.Select(peer =>
            new Observation(peer, peer.DownloadRate, peer.UploadRate, peer.Location)).ToArray();
        Origin = origin;
        Text = text;
        Cities = cities;
        _capacity = capacity;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    internal sealed record Snapshot(Observation[] Observations, Location? Origin, Strings Text, bool Cities, double Ceiling)
    {
        internal required object Subject { get; init; }
        internal bool Matches(Snapshot other) => Origin == other.Origin && Text.Language == other.Text.Language
            && Cities == other.Cities && Ceiling == other.Ceiling
            && (ReferenceEquals(Observations, other.Observations) || Observations.Length == other.Observations.Length
                && !Observations.Where((observation, index) => !observation.Matches(other.Observations[index])).Any());
    }

    internal sealed record Presentation(Dictionary<string, Group> Countries, List<Destination> Destinations, GeoPoint? Origin, double Ceiling)
    {
        internal required object Subject { get; init; }
        internal float Meridian => (float)(Origin?.Longitude ?? 0);
        internal bool Cities { get; init; }

        internal Dictionary<PeerPlace, int> Marks(IReadOnlyList<Location?> selected)
        {
            if (selected.Count == 0)
                return [];
            Dictionary<PeerPlace, int> counts = [];
            foreach (var location in selected)
            {
                if (location is not null)
                {
                    var place = PeerPlace.Of(location, Cities);
                    counts[place] = counts.GetValueOrDefault(place) + 1;
                }
            }
            Dictionary<PeerPlace, int> marks = [];
            foreach (var destination in Destinations)
            {
                var group = destination.Group;
                if (!marks.ContainsKey(group.Place) && counts.TryGetValue(group.Place, out var count))
                    marks[group.Place] = group.Regions.Length == 0 ? 1 : Reach(group.Regions, count);
            }
            return marks;
        }
    }

    internal Snapshot Capture() => new(_observations, Origin, Text!, Cities, Ceiling) { Subject = Subject };

    internal void Apply(Presentation presentation)
    {
        Countries = presentation.Countries;
        Destinations = presentation.Destinations;
        OriginPoint = presentation.Origin;
    }

    internal sealed class Arrangement
    {
        private Observation[] _observations = [];
        private object _subject = new();
        private (Group? Country, Group? Place)[]? _membership;
        private IReadOnlyDictionary<string, Geography.Country>? _world;
        private readonly Dictionary<PeerPlace, Group> _places = [];
        private readonly Dictionary<string, Geography.Area[]> _regions = [];
        private (float Meridian, float Scale) _regionsFor;
        internal Dictionary<string, Group> Countries { get; } = [];
        internal List<Destination> Destinations { get; } = [];
        internal Location? Origin { get; private set; }
        internal GeoPoint? OriginPoint { get; private set; }
        internal float Meridian => (float)(OriginPoint?.Longitude ?? 0);
        internal Strings? Text { get; private set; }
        internal bool Cities { get; private set; }
        internal double Ceiling { get; private set; }

        internal void Update(Snapshot snapshot)
        {
            _subject = snapshot.Subject;
            if (Cities != snapshot.Cities || Text?.Language != snapshot.Text.Language
                || _observations.Length != snapshot.Observations.Length
                || _observations.Where((observation, index) => observation.Location != snapshot.Observations[index].Location).Any())
                _membership = null;
            _observations = snapshot.Observations;
            Origin = snapshot.Origin;
            Text = snapshot.Text;
            Cities = snapshot.Cities;
            Ceiling = snapshot.Ceiling;
        }

        internal Presentation Present()
        {
            var copies = Countries.Values.Concat(_places.Values).Distinct().ToDictionary(group => group, group => group.Copy());
            return new(Countries.ToDictionary(entry => entry.Key, entry => copies[entry.Value]),
                Destinations.Select(destination => destination with { Group = copies[destination.Group] }).ToList(), OriginPoint, Ceiling)
                { Cities = Cities, Subject = _subject };
        }

        internal void Clear()
        {
            _observations = [];
            _membership = null;
            _world = null;
            Countries.Clear();
            Destinations.Clear();
            _places.Clear();
            _regions.Clear();
        }

        /// <summary>
        /// Groups the observed peers and places their destinations on the map
        /// fitted at <paramref name="scale"/> DIPs per degree.
        /// </summary>
        internal void Arrange(IReadOnlyDictionary<string, Geography.Country> world, float scale)
        {
            OriginPoint = Locate(Origin, world);
            if (!ReferenceEquals(_world, world))
            {
                _world = world;
                _membership = null;
                _regions.Clear();
            }
            if (_regionsFor != (Meridian, scale))
            {
                _regions.Clear();
                _regionsFor = (Meridian, scale);
            }
            if (_membership is null)
            {
                Countries.Clear();
                _places.Clear();
                _membership = new (Group?, Group?)[_observations.Length];
                for (var index = 0; index < _observations.Length; index++)
                {
                    var observation = _observations[index];
                    if (observation.Location is not { } location || Locate(location, world) is not { } point)
                        continue;
                    var code = location.Code;
                    var country = world.GetValueOrDefault(code);
                    if (!Countries.TryGetValue(code, out var group))
                    {
                        var language = Text!.Language;
                        group = new Group(
                            PeerPlace.Of(location, false),
                            () => location.CountryName(language),
                            country?.Anchor ?? point,
                            estimated: !Cities
                        );
                        Countries.Add(code, group);
                    }
                    Group? destination = null;
                    if (Cities)
                    {
                        var place = PeerPlace.Of(location, true);
                        if (!_places.TryGetValue(place, out destination))
                        {
                            var text = Text!;
                            destination = new Group(place, () => location.City?.Name(text.Language) is { } city
                                ? text.Format("peers", "location_city", city, location.CountryName(text.Language))
                                : location.CountryName(text.Language), point);
                            _places.Add(place, destination);
                        }
                    }
                    _membership[index] = (group, destination);
                }
            }
            foreach (var group in Countries.Values.Concat(_places.Values))
                group.Reset();
            for (var index = 0; index < _observations.Length; index++)
            {
                var membership = _membership[index];
                membership.Country?.Add(_observations[index]);
                membership.Place?.Add(_observations[index]);
            }
            var placed = 0;
            var rank = 0;
            IEnumerable<Group> destinations = Cities ? _places.Values : Countries.Values;
            foreach (var group in destinations)
            {
                rank = 0;
                if (
                    !group.Estimated
                    || !world.TryGetValue(group.Code, out var country)
                    || country.Areas.Length == 0
                )
                {
                    Place(group, group.Point, 1, null);
                    continue;
                }
                if (!_regions.TryGetValue(group.Code, out var regions))
                {
                    regions = Regions(country.Areas, Meridian, scale);
                    _regions[group.Code] = regions;
                }
                group.Regions = regions;
                // Idle peers are placed like one connection, at the most likely region.
                foreach (var (region, share) in Distribute(group.Regions, Math.Max(group.Active, 1)))
                    Place(group, region.Point, share, region.Name);
            }
            if (placed < Destinations.Count)
                Destinations.RemoveRange(placed, Destinations.Count - placed);

            void Place(Group group, GeoPoint point, double share, string? area)
            {
                var previous = placed < Destinations.Count ? Destinations[placed] : null;
                if (previous is null || !ReferenceEquals(previous.Group, group) || previous.Point != point
                    || previous.Share != share || previous.Area != area || previous.Rank != rank)
                {
                    var destination = new Destination(group, point, share, area) { Rank = rank };
                    if (placed < Destinations.Count)
                        Destinations[placed] = destination;
                    else
                        Destinations.Add(destination);
                }
                placed++;
                rank++;
            }
        }

        private static GeoPoint? Locate(Location? location, IReadOnlyDictionary<string, Geography.Country> world) =>
            location?.Point ?? (location is null ? null : world.GetValueOrDefault(location.Code)?.Anchor);

        // Bundled places closer than this, in DIPs on the fitted map, form one region.
        private const float Spacing = 25;

        /// <summary>
        /// A country's populated places, with places that would overlap on the
        /// fitted map merged into regions, most populous first.
        /// </summary>
        private static Geography.Area[] Regions(Geography.Area[] areas, float meridian, float scale)
        {
            List<Geography.Area> regions = [];
            List<Vector2> positions = [];
            foreach (var area in areas)
            {
                var position = MapViewport.Project(area.Point, meridian, scale);
                var nearby = -1;
                for (var index = 0; index < positions.Count && nearby < 0; index++)
                {
                    if (Vector2.Distance(positions[index], position) < Spacing)
                        nearby = index;
                }
                if (nearby < 0)
                {
                    regions.Add(area);
                    positions.Add(position);
                }
                else
                    regions[nearby] = regions[nearby] with
                    {
                        Population = regions[nearby].Population + area.Population,
                    };
            }
            return [.. regions.OrderByDescending(region => region.Population)];
        }

    }

    internal string Describe(Destination? destination)
    {
        var text = Text!;
        var group = destination?.Group;
        string value;
        if (group is null)
        {
            value = text.Get(
                "peer_map",
                OriginPoint is null ? "origin_unknown" : "approximate"
            );
        }
        else
        {
            value = text.Format(
                "peer_map",
                "detail",
                group.Name,
                group.Count,
                group.Active,
                text.Rate(group.Down),
                text.Rate(group.Up),
                group.Seeds,
                group.Leechers,
                group.Unknown
            );
            if (destination?.Area is { } area)
            {
                value +=
                    " · "
                    + text.Format(
                        "peer_map",
                        "estimated",
                        area,
                        text.Rate(destination.Down),
                        text.Rate(destination.Up)
                    );
            }
            else
            {
                var precision = group.Estimated ? "country_only" : "location_approximate";
                value += " · " + text.Get("peer_map", precision);
            }
        }

        return value;
    }

    /// <summary>
    /// How many regions, most populous first, the most probable placement of
    /// <paramref name="connections"/> peers uses.
    /// </summary>
    // Each next connection goes to the region with the highest population per
    // connection already there, counting the new one (the D'Hondt rule), and a
    // tie goes to the more populous region. That builds the most probable
    // placement, and a region never gets its first connection before a more
    // populous one, so the regions used are a prefix. For 90% and 10% regions,
    // a second connection stays in the first region (81% likely) instead of
    // spreading out (18%). Region i first gets a connection once every more
    // populous region j holds floor(Pj / Pi): the tenth, for 90% and 10%.
    internal static int Reach(Geography.Area[] regions, int connections)
    {
        var reached = 0;
        while (reached < regions.Length)
        {
            long first = 1;
            for (var index = 0; index < reached && first <= connections; index++)
                first += (long)Math.Floor(regions[index].Population / regions[reached].Population);
            if (first > connections)
                break;
            reached++;
        }
        return reached;
    }

    /// <summary>
    /// The regions a country's active peers most likely use, most populous
    /// first, with each region's share of their traffic.
    /// </summary>
    private static IEnumerable<(Geography.Area Region, double Share)> Distribute(
        Geography.Area[] regions,
        int connections
    )
    {
        var used = regions[..Reach(regions, connections)];
        var total = used.Sum(region => region.Population);
        return used.Select(region => (region, region.Population / total));
    }


    // Only the inspected place shows its name, so a group formats it when first read.
    internal sealed class Group(
        PeerPlace place,
        Func<string> name,
        GeoPoint point,
        bool estimated = false
    )
    {
        internal Group Copy() => (Group)MemberwiseClone();
        internal PeerPlace Place { get; } = place;
        internal string Code => Place.Code;
        internal string Name => field ??= name();
        internal GeoPoint Point { get; } = point;
        internal bool Estimated { get; private set; } = estimated;
        internal int Count { get; private set; }
        internal int Seeds { get; private set; }
        internal int Leechers { get; private set; }
        internal int Unknown => Count - Seeds - Leechers;
        internal int Connections { get; private set; }
        internal int Active { get; private set; }
        internal double Down { get; private set; }
        internal double Up { get; private set; }

        /// <summary>Whether it downloads at least as much as it uploads, which sets its arc and glow colour.</summary>
        internal bool Downloads => Down >= Up;

        /// <summary>
        /// The regions its estimated distribution places peers in, most populous
        /// first; empty when the group has no estimated distribution.
        /// </summary>
        internal Geography.Area[] Regions { get; set; } = [];

        internal void Reset()
        {
            Count = Seeds = Leechers = Connections = Active = 0;
            Down = Up = 0;
        }

        internal void Add(Observation observation)
        {
            Count++;
            if (observation.Role == PeerRole.Seed)
                Seeds++;
            else if (observation.Role == PeerRole.Leecher)
                Leechers++;
            Estimated |= observation.Location?.Point is null;
            if (observation.State != PeerState.Known)
                Connections++;
            if (observation.Down + observation.Up > 0)
                Active++;
            Down += observation.Down;
            Up += observation.Up;
        }
    }


    internal readonly record struct DestinationKey(string Code, GeoPoint? Point, bool Estimated);

    internal sealed record Destination(Group Group, GeoPoint Point, double Share, string? Area)
    {
        internal DestinationKey Key => new(Group.Code, Point, Group.Estimated);
        internal double Down => Group.Down * Share;
        internal double Up => Group.Up * Share;
        internal double Rate => Down + Up;

        /// <summary>Rank in the place's estimated distribution; zero for supplied coordinates.</summary>
        internal int Rank { get; init; }
    }

    internal sealed record Observation(Peer Peer, double Down, double Up, Location? Location)
    {
        internal PeerRole Role { get; } = Peer.Role;
        internal PeerState State { get; } = Peer.State;

        internal bool Matches(Observation other) => Down == other.Down && Up == other.Up
            && Location == other.Location && Role == other.Role && State == other.State;
    }
}
