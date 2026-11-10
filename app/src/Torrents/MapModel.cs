using System.Collections.Frozen;
using System.ComponentModel;
using System.Numerics;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shell;

namespace Syno.TinyTorrent.Torrents;

internal sealed class MapModel(Inspector owner) : INotifyPropertyChanged
{
    private Population _population = new(owner.PeerSubject, []);
    // The replies that changed the population since the map's worker last
    // took it, and how many peers they name.
    private List<Swarm.Reply> _pending = [];
    private int _pendingCount;
    // Changes when the worker must group every peer of the population again.
    private int _generation;
    private string? _language;
    internal object Subject => owner.PeerSubject;
    internal int PeerVersion => owner.PeerVersion;
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

    /// <summary>Takes the map's facts, and tells the map only when one changed.</summary>
    /// <param name="capacity">The connection capacity, download plus upload, in bytes per second, or null when unknown.</param>
    internal void Update(
        Population population,
        Location? origin,
        Strings text,
        bool cities = true,
        double? capacity = null
    )
    {
        var changed = Take(population)
            || origin != Origin
            || text.Language != _language
            || cities != Cities
            || capacity != _capacity;
        Origin = origin;
        Text = text;
        _language = text.Language;
        Cities = cities;
        _capacity = capacity;
        if (changed)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }

    // Returns whether the map shows other peers or other facts of them.
    private bool Take(Population population)
    {
        var previous = _population;
        if (ReferenceEquals(population, previous))
        {
            return false;
        }
        _population = population;
        // One empty population after another of the same subject shows the same map.
        if (population.Peers.Count == 0 && previous.Peers.Count == 0
            && ReferenceEquals(population.Subject, previous.Subject))
        {
            return false;
        }
        // Changes that name more peers than the population holds cost more
        // than grouping it again.
        if (population.Changes is { } changes && population.Follows == previous.Number)
        {
            var count = _pendingCount + changes.Sum(Size);
            if (count <= population.Peers.Count)
            {
                _pending.AddRange(changes);
                _pendingCount = count;
                return true;
            }
        }
        _generation++;
        _pending.Clear();
        _pendingCount = 0;
        return true;
    }

    // The peers a reply names, each of which the worker takes.
    private static int Size(Swarm.Reply reply) => reply.Changes.Count + reply.Joined.Count + reply.Left.Count;

    // The peer facts a map observation holds.
    private const Peer.Properties Observed =
        Peer.Properties.Role | Peer.Properties.State | Peer.Properties.DownloadRate | Peer.Properties.UploadRate
        | Peer.Located;

    /// <summary>Whether <paramref name="reply"/> changed a peer the map shows, or which peers it shows.</summary>
    internal static bool Affects(Swarm.Reply reply)
    {
        if (reply.Joined.Count > 0 || reply.Left.Count > 0)
        {
            return true;
        }
        foreach (var (_, change) in reply.Changes)
        {
            if ((change.Properties & Observed) != 0)
            {
                return true;
            }
        }
        return false;
    }

    /// <param name="Peers">The view's peers, a list that never changes once given.</param>
    internal sealed record Population(object Subject, IReadOnlyList<Peer> Peers)
    {
        private static long _made;

        internal long Number { get; } = Interlocked.Increment(ref _made);

        /// <summary>
        /// The replies, in order, that changed the view since the population
        /// numbered <see cref="Follows"/>, or null when the map must group every
        /// peer again. The map groups them again as well when it was last given
        /// another population. A peer that leaves never returns: a later reply
        /// makes a new peer for its endpoint.
        /// </summary>
        internal IReadOnlyList<Swarm.Reply>? Changes { get; init; }

        internal long Follows { get; init; }
    }

    internal sealed record Selection(bool HasPeers, bool Cities, FrozenDictionary<PeerPlace, int> Counts)
    {
        internal static readonly Selection Empty = new(false, false, FrozenDictionary<PeerPlace, int>.Empty);
    }

    /// <param name="Changes">The replies the worker has not taken, or null when there are none.</param>
    /// <param name="Generation">Changes when the worker must group <paramref name="Peers"/> again.</param>
    internal sealed record Snapshot(
        IReadOnlyList<Peer> Peers,
        IReadOnlyList<Swarm.Reply>? Changes,
        int Generation,
        Location? Origin,
        Strings Text,
        bool Cities,
        double Ceiling
    )
    {
        internal required object Subject { get; init; }
    }

    internal sealed record Presentation(Dictionary<string, Group> Countries, List<Destination> Destinations, GeoPoint? Origin, double Ceiling)
    {
        internal required object Subject { get; init; }
        internal float Meridian => (float)(Origin?.Longitude ?? 0);
        internal bool Cities { get; init; }

        /// <summary>Each destination by its key, made once for every picture drawn from this presentation.</summary>
        internal Dictionary<DestinationKey, Destination> ByKey { get; } =
            Destinations.ToDictionary(destination => destination.Key);

        /// <summary>
        /// Whether this draws the same places with the same facts as
        /// <paramref name="other"/>, as a regroup of equal peer facts does.
        /// Group names are not compared, so the caller checks the language.
        /// </summary>
        internal bool Matches(Presentation other)
        {
            if (!ReferenceEquals(Subject, other.Subject) || Cities != other.Cities || Origin != other.Origin
                || Ceiling != other.Ceiling || Destinations.Count != other.Destinations.Count
                || Countries.Count != other.Countries.Count)
            {
                return false;
            }
            for (var index = 0; index < Destinations.Count; index++)
            {
                var mine = Destinations[index];
                var theirs = other.Destinations[index];
                if (!ReferenceEquals(mine, theirs)
                    && (mine.Point != theirs.Point || mine.Share != theirs.Share || mine.Area != theirs.Area
                        || mine.Rank != theirs.Rank || !mine.Group.Matches(theirs.Group)))
                {
                    return false;
                }
            }
            foreach (var (code, group) in Countries)
            {
                if (!other.Countries.TryGetValue(code, out var theirs) || !group.Matches(theirs))
                    return false;
            }
            return true;
        }

        internal Dictionary<PeerPlace, int> Marks(Selection selected)
        {
            if (selected.Counts.Count == 0)
                return [];
            Dictionary<PeerPlace, int> marks = [];
            foreach (var destination in Destinations)
            {
                var group = destination.Group;
                if (!marks.ContainsKey(group.Place) && selected.Counts.TryGetValue(group.Place, out var count))
                    marks[group.Place] = group.Regions.Length == 0 ? 1 : Reach(group.Regions, count);
            }
            return marks;
        }
    }

    /// <summary>
    /// The facts the map's worker groups, with the changes it has not taken;
    /// null while the population belongs to an earlier subject. The worker must
    /// prepare the snapshot it is given, because each change is handed over once.
    /// </summary>
    internal Snapshot? Capture()
    {
        if (!ReferenceEquals(_population.Subject, Subject))
            return null;
        List<Swarm.Reply>? changes = null;
        if (_pending.Count > 0)
        {
            changes = _pending;
            _pending = [];
            _pendingCount = 0;
        }
        return new(_population.Peers, changes, _generation, Origin, Text!, Cities, Ceiling)
        {
            Subject = _population.Subject,
        };
    }

    internal void Apply(Presentation presentation)
    {
        Countries = presentation.Countries;
        Destinations = presentation.Destinations;
        OriginPoint = presentation.Origin;
    }

    /// <summary>
    /// The map's groups, kept on its worker between snapshots. Each peer's
    /// contribution is kept, so a change takes exactly that off its groups and
    /// adds the peer's current values, however many updates the worker missed.
    /// </summary>
    internal sealed class Arrangement(IReadOnlyDictionary<string, Geography.Country> world)
    {
        private IReadOnlyDictionary<string, Geography.Country> _world = world;
        private object _subject = new();
        private int _generation;
        private readonly Dictionary<Peer, Contribution> _contributions = [];
        private readonly Dictionary<PeerPlace, Group> _places = [];
        // The presentations read each group's copy on other threads while this
        // worker changes the group, so a group is copied again only after it changed.
        private readonly Dictionary<Group, Group> _copies = [];
        private readonly HashSet<Group> _changedGroups = [];
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

        /// <summary>Whether anything a presentation shows changed since the last one.</summary>
        internal bool Changed { get; private set; }

        private readonly record struct Contribution(Observation Observation, Group? Country, Group? Place);

        /// <summary>
        /// Takes the snapshot's changes, or groups its peers again for a new
        /// subject, detail, language or generation. Only grouping again stops
        /// at <paramref name="cancellation"/>, because the next snapshot groups
        /// its peers again, while each change is handed over once.
        /// </summary>
        internal void Update(Snapshot snapshot, CancellationToken cancellation)
        {
            var regroups = snapshot.Generation != _generation || !ReferenceEquals(snapshot.Subject, _subject)
                || snapshot.Cities != Cities || snapshot.Text.Language != Text?.Language;
            Changed |= regroups || snapshot.Origin != Origin || snapshot.Ceiling != Ceiling;
            _generation = snapshot.Generation;
            Origin = snapshot.Origin;
            Text = snapshot.Text;
            Cities = snapshot.Cities;
            Ceiling = snapshot.Ceiling;
            if (regroups)
            {
                // Until the groups are whole they belong to no subject.
                _subject = new();
                _contributions.Clear();
                Countries.Clear();
                _places.Clear();
                _copies.Clear();
                _changedGroups.Clear();
                foreach (var peer in snapshot.Peers)
                {
                    cancellation.ThrowIfCancellationRequested();
                    Join(peer);
                }
            }
            else if (snapshot.Changes is { } replies)
            {
                // The replies apply in the order they came, so a peer that
                // joined and left since the last snapshot ends outside the map.
                foreach (var reply in replies)
                {
                    foreach (var peer in reply.Joined)
                    {
                        Join(peer);
                    }
                    foreach (var (peer, change) in reply.Changes)
                    {
                        if ((change.Properties & Observed) != 0)
                        {
                            Move(peer);
                        }
                    }
                    foreach (var peer in reply.Left)
                    {
                        Leave(peer);
                    }
                }
            }
            _subject = snapshot.Subject;
            Changed |= _changedGroups.Count > 0;
        }

        private void Join(Peer peer)
        {
            if (!_contributions.ContainsKey(peer))
                _contributions.Add(peer, Contribute(new(peer.Capture(), peer.Location)));
        }

        // A peer the map does not hold, because it left or never joined the view, stays out.
        private void Move(Peer peer)
        {
            if (!_contributions.TryGetValue(peer, out var contribution))
                return;
            var observation = new Observation(peer.Capture(), peer.Location);
            var previous = contribution.Observation;
            if (observation == previous)
                return;
            if (observation.Location == previous.Location)
            {
                // The same place keeps its groups, even when this is their only peer.
                Replace(contribution.Country, previous, observation);
                Replace(contribution.Place, previous, observation);
                _contributions[peer] = contribution with { Observation = observation };
                return;
            }
            Withdraw(contribution);
            _contributions[peer] = Contribute(observation);
        }

        private void Leave(Peer peer)
        {
            if (_contributions.Remove(peer, out var contribution))
                Withdraw(contribution);
        }

        private Contribution Contribute(Observation observation)
        {
            if (observation.Location is not { } location || Locate(location, _world) is not { } point)
                return new(observation, null, null);
            var country = CountryOf(location, point);
            country.Add(observation);
            _changedGroups.Add(country);
            Group? place = null;
            if (Cities)
            {
                place = PlaceOf(location, point);
                place.Add(observation);
                _changedGroups.Add(place);
            }
            return new(observation, country, place);
        }

        private void Replace(Group? group, Observation previous, Observation observation)
        {
            if (group is null)
                return;
            group.Remove(previous);
            group.Add(observation);
            _changedGroups.Add(group);
        }

        private void Withdraw(Contribution contribution)
        {
            Withdraw(contribution.Country, contribution.Observation);
            Withdraw(contribution.Place, contribution.Observation);
        }

        private void Withdraw(Group? group, Observation observation)
        {
            if (group is null)
                return;
            group.Remove(observation);
            if (group.Count > 0)
            {
                _changedGroups.Add(group);
                return;
            }
            if (group.Place.Cities)
                _places.Remove(group.Place);
            else
                Countries.Remove(group.Code);
            _changedGroups.Remove(group);
            _copies.Remove(group);
        }

        private Group CountryOf(Location location, GeoPoint point)
        {
            if (!Countries.TryGetValue(location.Code, out var group))
            {
                group = NewCountry(location, point);
                Countries.Add(location.Code, group);
            }
            return group;
        }

        // NewCountry and NewPlace are apart from their lookups because C# allocates
        // a lambda's captured parameters when the method starts. The lookups run
        // once for every peer grouped, and only a new group needs the lambda.
        private Group NewCountry(Location location, GeoPoint point)
        {
            var language = Text!.Language;
            return new Group(
                PeerPlace.Of(location, false),
                () => location.CountryName(language),
                _world.GetValueOrDefault(location.Code)?.Anchor ?? point,
                estimated: !Cities
            );
        }

        private Group PlaceOf(Location location, GeoPoint point)
        {
            var place = PeerPlace.Of(location, true);
            if (!_places.TryGetValue(place, out var group))
            {
                group = NewPlace(place, location, point);
                _places.Add(place, group);
            }
            return group;
        }

        private Group NewPlace(PeerPlace place, Location location, GeoPoint point)
        {
            var text = Text!;
            return new Group(place, () => location.City?.Name(text.Language) is { } city
                ? text.Format("peers", "location_city", city, location.CountryName(text.Language))
                : location.CountryName(text.Language), point);
        }

        internal Presentation Present()
        {
            Changed = false;
            var countries = new Dictionary<string, Group>(Countries.Count);
            foreach (var (code, group) in Countries)
                countries.Add(code, _copies[group]);
            return new(countries, [.. Destinations], OriginPoint, Ceiling) { Cities = Cities, Subject = _subject };
        }

        internal void Clear()
        {
            _world = FrozenDictionary<string, Geography.Country>.Empty;
            _contributions.Clear();
            Countries.Clear();
            Destinations.Clear();
            _places.Clear();
            _copies.Clear();
            _changedGroups.Clear();
            _regions.Clear();
        }

        /// <summary>
        /// Places the groups' destinations on the map fitted at
        /// <paramref name="scale"/> DIPs per degree.
        /// </summary>
        internal void Arrange(float scale)
        {
            OriginPoint = Locate(Origin, _world);
            if (_regionsFor != (Meridian, scale))
            {
                _regions.Clear();
                _regionsFor = (Meridian, scale);
            }
            IEnumerable<Group> destinations = Cities ? _places.Values : Countries.Values;
            foreach (var group in destinations)
            {
                var regions = RegionsOf(group, scale);
                if (!ReferenceEquals(group.Regions, regions))
                {
                    group.Regions = regions;
                    _changedGroups.Add(group);
                }
            }
            foreach (var group in _changedGroups)
                _copies[group] = group.Copy();
            _changedGroups.Clear();
            var placed = 0;
            var rank = 0;
            foreach (var kept in destinations)
            {
                var group = _copies[kept];
                rank = 0;
                if (group.Regions.Length == 0)
                {
                    Place(group, group.Point, 1, null);
                    continue;
                }
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

        // The regions an estimated group's traffic is spread over; none for a
        // group whose peers all have coordinates, or a country without places.
        private Geography.Area[] RegionsOf(Group group, float scale)
        {
            if (!group.Estimated || !_world.TryGetValue(group.Code, out var country) || country.Areas.Length == 0)
                return [];
            if (!_regions.TryGetValue(group.Code, out var regions))
            {
                regions = Regions(country.Areas, Meridian, scale);
                _regions[group.Code] = regions;
            }
            return regions;
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
        // Its peers without coordinates of their own.
        private int _unpointed;

        internal Group Copy() => (Group)MemberwiseClone();
        internal PeerPlace Place { get; } = place;
        internal string Code => Place.Code;
        internal string Name => field ??= name();
        internal GeoPoint Point { get; } = point;

        /// <summary>
        /// Whether its traffic is placed by estimate: always for a country in
        /// Countries detail, and otherwise while a peer has no coordinates.
        /// </summary>
        internal bool Estimated => estimated || _unpointed > 0;

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

        // The facts the map draws and describes, apart from the name.
        internal bool Matches(Group other) =>
            ReferenceEquals(this, other)
            || Place == other.Place && Point == other.Point && Estimated == other.Estimated
                && Count == other.Count && Seeds == other.Seeds && Leechers == other.Leechers
                && Connections == other.Connections && Active == other.Active
                && Down == other.Down && Up == other.Up && Regions.SequenceEqual(other.Regions);

        internal void Add(Observation observation) => Tally(observation, 1);

        // Exact: the engine sends rates as whole bytes per second.
        internal void Remove(Observation observation) => Tally(observation, -1);

        private void Tally(Observation observation, int sign)
        {
            Count += sign;
            if (observation.Role == PeerRole.Seed)
                Seeds += sign;
            else if (observation.Role == PeerRole.Leecher)
                Leechers += sign;
            if (observation.Location?.Point is null)
                _unpointed += sign;
            if (observation.State != PeerState.Known)
                Connections += sign;
            if (observation.Down + observation.Up > 0)
                Active += sign;
            Down += sign * observation.Down;
            Up += sign * observation.Up;
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

    internal readonly record struct Observation(Location? Location, double Down, double Up, PeerRole Role, PeerState State)
    {
        internal Observation(Peer.Record record, Location? location)
            : this(location, record.Values.DownloadRate, record.Values.UploadRate, record.Values.Role, record.Values.State) { }
    }
}
