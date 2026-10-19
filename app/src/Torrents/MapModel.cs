using System.ComponentModel;
using System.Numerics;
using Syno.TinyTorrent.Shared;

namespace Syno.TinyTorrent.Torrents;

internal sealed class MapModel(Inspector owner) : INotifyPropertyChanged
{
    private Observation[] _observations = [];
    private string _language = string.Empty;
    internal Dictionary<string, Group> Countries { get; } = [];
    internal List<Destination> Destinations { get; } = [];
    internal Location? Origin { get; private set; }
    internal GeoPoint? OriginPoint { get; private set; }

    /// <summary>The longitude at the map's centre: the origin's, or 0° while it is unknown.</summary>
    internal float Meridian => (float)(OriginPoint?.Longitude ?? 0);

    internal Strings? Text { get; private set; }
    internal bool Cities { get; private set; }
    internal PeerPlace? Place => owner.Place;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void RefreshPlace() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Place)));

    internal void Update(IReadOnlyList<Peer>? peers, Location? origin, Strings text, bool cities = true)
    {
        peers ??= [];
        // Compare mutable peer facts with the last snapshot before allocating another.
        if (IsShowing(peers) && Origin == origin && _language == text.Language && Cities == cities)
            return;
        _observations = peers.Select(peer =>
            new Observation(peer, peer.DownloadRate, peer.UploadRate, peer.Location)).ToArray();
        Origin = origin;
        Text = text;
        _language = text.Language;
        Cities = cities;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    internal IEnumerable<Peer> Peers => _observations.Select(observation => observation.Peer);

    /// <summary>
    /// Groups the observed peers and places their destinations on the map
    /// fitted at <paramref name="scale"/> DIPs per degree.
    /// </summary>
    internal void Arrange(IReadOnlyDictionary<string, Geography.Country> world, float scale)
    {
        Destinations.Clear();
        OriginPoint = Locate(Origin, world);
        Countries.Clear();
        Dictionary<PeerPlace, Group> locations = [];
        foreach (var observation in _observations)
        {
            if (
                observation.Location is not { } location
                || Locate(location, world) is not { } point
            )
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
            group.Add(observation);
            if (!Cities)
                continue;
            var place = PeerPlace.Of(location, true);
            if (!locations.TryGetValue(place, out var destination))
            {
                var peer = observation.Peer;
                destination = new Group(place, () => peer.LocationText, point);
                locations.Add(place, destination);
            }
            destination.Add(observation);
        }
        IEnumerable<Group> destinations = Cities ? locations.Values : Countries.Values;
        foreach (var group in destinations)
        {
            if (
                !group.Estimated
                || !world.TryGetValue(group.Code, out var country)
                || country.Areas.Length == 0
            )
            {
                Destinations.Add(new Destination(group, group.Point, 1, null));
                continue;
            }
            group.Regions = Regions(country.Areas, Meridian, scale);
            var rank = 0;
            // Idle peers are placed like one connection, at the most likely region.
            foreach (var (region, share) in Distribute(group.Regions, Math.Max(group.Active, 1)))
                Destinations.Add(new Destination(group, region.Point, share, region.Name) { Rank = rank++ });
        }
    }

    private static GeoPoint? Locate(Location? location, IReadOnlyDictionary<string, Geography.Country> world) =>
        location?.Point ?? (location is null ? null : world.GetValueOrDefault(location.Code)?.Anchor);

    /// <summary>
    /// How many destinations of each place the <paramref name="selected"/>
    /// peers mark.
    /// </summary>
    internal Dictionary<PeerPlace, int> Marks(IReadOnlyList<Peer> selected)
    {
        if (selected.Count == 0)
            return [];
        Dictionary<PeerPlace, int> counts = [];
        foreach (var peer in selected)
        {
            if (peer.Location is { } location)
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
                text.Format("units", "rate", text.Bytes(group.Down)),
                text.Format("units", "rate", text.Bytes(group.Up)),
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
                        text.Format("units", "rate", text.Bytes(group.Down * destination.Share)),
                        text.Format("units", "rate", text.Bytes(group.Up * destination.Share))
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
        internal PeerPlace Place { get; } = place;
        internal string Code => Place.Code;
        internal string Name => field ??= name();
        internal GeoPoint Point { get; } = point;
        internal bool Estimated { get; private set; } = estimated;
        internal int Count { get; private set; }
        internal int Seeds { get; private set; }
        internal int Leechers { get; private set; }
        internal int Unknown => Count - Seeds - Leechers;
        internal int Active { get; private set; }
        internal double Down { get; private set; }
        internal double Up { get; private set; }

        /// <summary>
        /// The regions its estimated distribution places peers in, most populous
        /// first; empty when the group has no estimated distribution.
        /// </summary>
        internal Geography.Area[] Regions { get; set; } = [];

        internal void Add(Observation observation)
        {
            Count++;
            if (observation.Role == PeerRole.Seed)
                Seeds++;
            else if (observation.Role == PeerRole.Leecher)
                Leechers++;
            Estimated |= observation.Location?.Point is null;
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

        /// <summary>Rank in the place's estimated distribution; zero for supplied coordinates.</summary>
        internal int Rank { get; init; }
    }

    private bool IsShowing(IReadOnlyList<Peer> peers)
    {
        if (peers.Count != _observations.Length)
            return false;
        for (var index = 0; index < peers.Count; index++)
        {
            if (!_observations[index].Matches(peers[index]))
                return false;
        }
        return true;
    }

    internal sealed record Observation(Peer Peer, double Down, double Up, Location? Location)
    {
        internal PeerRole Role { get; } = Peer.Role;

        /// <summary>Whether the peer still has the values this observation recorded.</summary>
        internal bool Matches(Peer peer) =>
            ReferenceEquals(Peer, peer)
            && Down == peer.DownloadRate
            && Up == peer.UploadRate
            && Equals(Location, peer.Location)
            && Role == peer.Role;
    }
}
