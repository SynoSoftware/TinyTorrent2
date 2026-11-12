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
    internal IReadOnlySet<PeerPlace>? Places => owner.Places;
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

    internal void RefreshPlaces() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Places)));

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
                var mine = Destinations[index].Members;
                var theirs = other.Destinations[index].Members;
                if (ReferenceEquals(mine, theirs))
                    continue;
                if (mine.Count != theirs.Count)
                    return false;
                for (var member = 0; member < mine.Count; member++)
                {
                    if (!mine[member].Matches(theirs[member]))
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
                var members = destination.Members;
                for (var index = 0; index < members.Count; index++)
                {
                    var group = members[index].Group;
                    if (!marks.ContainsKey(group.Place) && selected.Counts.TryGetValue(group.Place, out var count))
                        marks[group.Place] = group.Regions.Length == 0 ? 1 : Reach(group.Regions, count);
                }
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
        // Where the groups place their peers, before close ones show as one destination.
        private readonly List<Placement> _placed = [];
        // What the last merge placed the destinations for; a change merges every placement again.
        private (object? Subject, bool Cities, float Meridian, Vector2 Shown, MapGrouping Grouping) _merged;
        // Kept for Merge's capacity: each placement's index by key, its point on the shown map,
        // its landing's index, the order the unplaced ones are taken in, the next landing in its
        // grid cell, the next member of its landing, each landing's last member, and the last
        // merge's destinations by key.
        private readonly Dictionary<DestinationKey, int> _indexes = [];
        private readonly List<Vector2> _positions = [];
        private readonly List<int> _landing = [];
        private readonly List<int> _order = [];
        private readonly List<int> _nextInCell = [];
        private readonly List<int> _nextMember = [];
        private readonly List<int> _lastMember = [];
        private readonly Dictionary<(int Column, int Row, bool Connected), int> _cells = [];
        private readonly Dictionary<DestinationKey, Destination> _previous = [];
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
                _contributions.EnsureCapacity(snapshot.Peers.Count);
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
            _placed.Clear();
            _previous.Clear();
            _places.Clear();
            _copies.Clear();
            _changedGroups.Clear();
            _regions.Clear();
        }

        /// <summary>
        /// Places the groups' destinations on the map fitted at <paramref name="scale"/> DIPs
        /// per degree, and shows the destinations that <paramref name="grouping"/> finds too close
        /// on the map shown at <paramref name="shown"/> DIPs per degree of longitude and latitude
        /// as one.
        /// </summary>
        internal void Arrange(float scale, Vector2 shown, MapGrouping grouping)
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
            if (placed < _placed.Count)
                _placed.RemoveRange(placed, _placed.Count - placed);
            Merge(shown, grouping);

            void Place(Group group, GeoPoint point, double share, string? area)
            {
                var previous = placed < _placed.Count ? _placed[placed] : null;
                if (previous is null || !ReferenceEquals(previous.Group, group) || previous.Point != point
                    || previous.Share != share || previous.Area != area || previous.Rank != rank)
                {
                    var placement = new Placement(group, point, share, area) { Rank = rank };
                    if (placed < _placed.Count)
                        _placed[placed] = placement;
                    else
                        _placed.Add(placement);
                }
                placed++;
                rank++;
            }
        }

        // Shows the placements closer than Distance on the shown map as one destination. The
        // placements with connections and the others merge only among themselves, each by its
        // own Distance. Each placement taken joins the nearest landing of its kind closer than
        // Distance, or becomes a landing, so landings lie at least Distance apart and a line of
        // close places cannot chain into one destination. It is measured straight on the map as
        // drawn at the default view: the seam there is the map's left and right edges, and
        // places on either side of it are drawn a map width apart. A grid of Distance-sized
        // cells for each kind finds the near landings, because the places can number in the
        // thousands. Called alone, it merges the placements of the last Arrange, so no fact,
        // fitted scale or origin may have changed.
        internal void Merge(Vector2 shown, MapGrouping grouping)
        {
            var count = _placed.Count;
            // A new scale, seam, subject, detail or grouping takes every placement, most peers
            // first, so each destination lands at its member with the most peers. Otherwise each
            // placement keeps its landing while that shows, so changing peer counts never move an
            // arc.
            var whole = _merged != (_subject, Cities, Meridian, shown, grouping);
            _merged = (_subject, Cities, Meridian, shown, grouping);
            _indexes.Clear();
            _positions.Clear();
            _landing.Clear();
            _order.Clear();
            _nextInCell.Clear();
            _nextMember.Clear();
            _lastMember.Clear();
            _cells.Clear();
            _previous.Clear();
            for (var index = 0; index < count; index++)
            {
                _indexes[_placed[index].Key] = index;
                _positions.Add(MapViewport.Coordinates(_placed[index].Point, Meridian) * shown);
                _landing.Add(-1);
                _nextInCell.Add(-1);
                _nextMember.Add(-1);
                _lastMember.Add(index);
            }
            foreach (var destination in Destinations)
            {
                _previous[destination.Key] = destination;
                if (whole || !_indexes.TryGetValue(destination.Key, out var landing))
                    continue;
                // A landing that gained or lost connections merges again by the other
                // Distance, and so does a member that changed kind.
                var connected = Connected(_placed[landing]);
                if (Connected(destination.Members[0]) != connected)
                    continue;
                var members = destination.Members;
                for (var position = 0; position < members.Count; position++)
                {
                    if (_indexes.TryGetValue(members[position].Key, out var index)
                        && Connected(_placed[index]) == connected)
                        _landing[index] = landing;
                }
                Land(landing);
            }
            for (var index = 0; index < count; index++)
            {
                if (_landing[index] < 0)
                    _order.Add(index);
            }
            // Equal peers go by key, the same way at every merge, unlike the groups' dictionary,
            // whose order changes when it reuses a freed slot.
            _order.Sort((first, second) =>
                Peers(_placed[second]).CompareTo(Peers(_placed[first])) is var order && order != 0
                    ? order
                    : _placed[first].Key.CompareTo(_placed[second].Key));
            foreach (var index in _order)
            {
                var nearest = Nearest(_positions[index], Connected(_placed[index]));
                if (nearest >= 0)
                    _landing[index] = nearest;
                else
                {
                    _landing[index] = index;
                    Land(index);
                }
            }
            // Members follow their landing in presentation order.
            for (var index = 0; index < count; index++)
            {
                var landing = _landing[index];
                if (landing == index)
                    continue;
                _nextMember[_lastMember[landing]] = index;
                _lastMember[landing] = index;
            }
            var shownCount = 0;
            for (var index = 0; index < count; index++)
            {
                if (_landing[index] != index)
                    continue;
                var destination = _previous.TryGetValue(_placed[index].Key, out var previous) && Unchanged(previous, index)
                    ? previous
                    : new Destination(Members(index), Text!);
                if (shownCount < Destinations.Count)
                    Destinations[shownCount] = destination;
                else
                    Destinations.Add(destination);
                shownCount++;
            }
            if (shownCount < Destinations.Count)
                Destinations.RemoveRange(shownCount, Destinations.Count - shownCount);
            _previous.Clear();
        }

        // How far apart destinations of one kind on the shown map may lie and still show as
        // one. Places with connections follow the place grouping, because each of their
        // destinations draws an arc. Places whose peers are only known draw no arc, so each
        // keeps its own dot and the map shows where the torrent is available.
        private float Distance(bool connected) => !connected ? DotSpacing : _merged.Grouping switch
        {
            MapGrouping.None => DotSpacing,
            MapGrouping.Small => GroupingSpacing / 2,
            MapGrouping.Large => GroupingSpacing * 2,
            _ => GroupingSpacing,
        };

        private static bool Connected(Placement placement) => placement.Group.Connections > 0;

        private (int Column, int Row, bool Connected) Cell(Vector2 position, bool connected)
        {
            var distance = Distance(connected);
            return ((int)MathF.Floor(position.X / distance), (int)MathF.Floor(position.Y / distance), connected);
        }

        private void Land(int index)
        {
            var cell = Cell(_positions[index], Connected(_placed[index]));
            _nextInCell[index] = _cells.GetValueOrDefault(cell, -1);
            _cells[cell] = index;
        }

        // The landing of the given kind closer than its Distance to position, or -1 when none is.
        private int Nearest(Vector2 position, bool connected)
        {
            var (column, row, _) = Cell(position, connected);
            var nearest = -1;
            var distance = Distance(connected);
            for (var across = -1; across <= 1; across++)
            {
                for (var down = -1; down <= 1; down++)
                {
                    for (var landing = _cells.GetValueOrDefault((column + across, row + down, connected), -1); landing >= 0; landing = _nextInCell[landing])
                    {
                        var apart = Vector2.Distance(position, _positions[landing]);
                        if (apart < distance)
                        {
                            distance = apart;
                            nearest = landing;
                        }
                    }
                }
            }
            return nearest;
        }

        // Whether destination shows exactly the placements the landing at index now holds.
        private bool Unchanged(Destination destination, int index)
        {
            var members = destination.Members;
            var position = 0;
            for (var member = index; member >= 0; member = _nextMember[member], position++)
            {
                if (position == members.Count || !ReferenceEquals(members[position], _placed[member]))
                    return false;
            }
            return position == members.Count;
        }

        private Placement[] Members(int index)
        {
            var count = 0;
            for (var member = index; member >= 0; member = _nextMember[member])
                count++;
            var members = new Placement[count];
            count = 0;
            for (var member = index; member >= 0; member = _nextMember[member])
                members[count++] = _placed[member];
            return members;
        }

        // An estimated placement holds its share of its group's peers.
        private static double Peers(Placement placement) => placement.Group.Count * placement.Share;

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

        // Bundled places closer than this many DIPs on the fitted map are too close to tell
        // apart, so they form one region.
        private const float Spacing = 25;

        // Places closer than this many DIPs on the shown map show as one dot, because their
        // dots would cover each other almost entirely.
        private const float DotSpacing = 1;

        // Destinations with connections closer than this many DIPs on the shown map show as
        // one at Medium place grouping.
        private const float GroupingSpacing = 8;

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
        string value;
        if (destination is null)
        {
            value = text.Get(
                "peer_map",
                OriginPoint is null ? "origin_unknown" : "approximate"
            );
        }
        else
        {
            var totals = destination.Totals;
            value = text.Format(
                "peer_map",
                "detail",
                destination.Name,
                totals.Count,
                totals.Active,
                text.Rate(totals.Down),
                text.Rate(totals.Up),
                totals.Seeds,
                totals.Leechers,
                totals.Unknown
            );
            // Only one region names where an estimate places its share.
            if (destination.Members is [{ Area: { } area } region])
            {
                value +=
                    " · "
                    + text.Format(
                        "peer_map",
                        "estimated",
                        area,
                        text.Rate(region.Down),
                        text.Rate(region.Up)
                    );
            }
            else
            {
                var precision = totals.Estimated ? "country_only" : "location_approximate";
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

        /// <summary>Whether it downloads at least as much as it uploads, which sets an estimated country's glow colour.</summary>
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
            if (observation.IsConnected)
                Connections += sign;
            if (observation.IsActive)
                Active += sign;
            Down += sign * observation.Down;
            Up += sign * observation.Up;
        }
    }


    internal readonly record struct DestinationKey(string Code, GeoPoint? Point, bool Estimated) : IComparable<DestinationKey>
    {
        public int CompareTo(DestinationKey other)
        {
            var order = string.CompareOrdinal(Code, other.Code);
            if (order == 0)
                order = Nullable.Compare(Point?.Longitude, other.Point?.Longitude);
            if (order == 0)
                order = Nullable.Compare(Point?.Latitude, other.Point?.Latitude);
            return order != 0 ? order : Estimated.CompareTo(other.Estimated);
        }
    }

    /// <summary>Where a group places peers: its coordinate, or one region of its estimated distribution.</summary>
    internal sealed record Placement(Group Group, GeoPoint Point, double Share, string? Area)
    {
        internal DestinationKey Key => new(Group.Code, Point, Group.Estimated);
        internal double Down => Group.Down * Share;
        internal double Up => Group.Up * Share;

        /// <summary>Rank in the place's estimated distribution; zero for supplied coordinates.</summary>
        internal int Rank { get; init; }

        // The facts the map draws and describes, apart from the group's name.
        internal bool Matches(Placement other) =>
            ReferenceEquals(this, other)
            || Point == other.Point && Share == other.Share && Area == other.Area && Rank == other.Rank
                && Group.Matches(other.Group);
    }

    /// <summary>The peers a destination shows: each of its places' peers once, with their whole rates.</summary>
    internal readonly record struct Totals
    {
        internal int Count { get; init; }
        internal int Seeds { get; init; }
        internal int Leechers { get; init; }
        internal int Unknown => Count - Seeds - Leechers;
        internal int Connections { get; init; }
        internal int Active { get; init; }
        internal double Down { get; init; }
        internal double Up { get; init; }

        /// <summary>Whether some of its peers are placed by estimate, so it is labelled country-only.</summary>
        internal bool Estimated { get; init; }

        internal Totals Add(Group group) => this with
        {
            Count = Count + group.Count,
            Seeds = Seeds + group.Seeds,
            Leechers = Leechers + group.Leechers,
            Connections = Connections + group.Connections,
            Active = Active + group.Active,
            Down = Down + group.Down,
            Up = Up + group.Up,
            Estimated = Estimated || group.Estimated,
        };
    }

    /// <summary>
    /// What the map shows at one point: the placements too close to tell apart at the shown
    /// zoom, its landing first. Its arc, light and dots stand for all of them.
    /// </summary>
    internal sealed class Destination
    {
        private readonly Strings _text;
        private readonly int _placeCount;

        /// <param name="members">The placements it shows, its landing first; never empty.</param>
        internal Destination(IReadOnlyList<Placement> members, Strings text)
        {
            Members = members;
            _text = text;
            for (var index = 0; index < members.Count; index++)
            {
                var member = members[index];
                Down += member.Down;
                Up += member.Up;
                if (!Counted(index))
                {
                    Totals = Totals.Add(member.Group);
                    _placeCount++;
                }
            }

            // The regions of one estimated place share its group, whose peers count once.
            bool Counted(int index)
            {
                for (var earlier = 0; earlier < index; earlier++)
                {
                    if (ReferenceEquals(members[earlier].Group, members[index].Group))
                        return true;
                }
                return false;
            }
        }

        internal IReadOnlyList<Placement> Members { get; }
        internal DestinationKey Key => Members[0].Key;
        internal GeoPoint Point => Members[0].Point;

        /// <summary>Its landing's name, with the number of its other places.</summary>
        internal string Name => field ??= _placeCount == 1
            ? Members[0].Group.Name
            : _text.FormatCount("peer_map", "places_more", _placeCount - 1, Members[0].Group.Name);

        internal Totals Totals { get; }

        /// <summary>The rates its arc and dots draw: each member's share of its group's rates.</summary>
        internal double Down { get; }

        internal double Up { get; }
        internal double Rate => Down + Up;

        /// <summary>Whether it draws at least as much download as upload, which sets its arc and glow colour.</summary>
        internal bool Downloads => Down >= Up;

        /// <summary>Whether it shows the peers of any of <paramref name="places"/>.</summary>
        internal bool Holds(IReadOnlySet<PeerPlace> places)
        {
            for (var index = 0; index < Members.Count; index++)
            {
                if (places.Contains(Members[index].Group.Place))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Whether it shows a place of the selected peers within the regions their estimated
        /// distribution reaches, given each selected place's reach (<see cref="Presentation.Marks"/>).
        /// </summary>
        internal bool Marked(IReadOnlyDictionary<PeerPlace, int> marks)
        {
            for (var index = 0; index < Members.Count; index++)
            {
                var member = Members[index];
                if (marks.GetValueOrDefault(member.Group.Place) > member.Rank)
                    return true;
            }
            return false;
        }
    }

    internal readonly record struct Observation
    {
        internal Observation(Peer.Record record, Location? location)
        {
            Location = location;
            Down = record.Values.DownloadRate;
            Up = record.Values.UploadRate;
            Role = record.Values.Role;
            IsConnected = record.Values.IsConnected;
            IsActive = record.Values.IsActive;
        }

        internal Location? Location { get; }
        internal double Down { get; }
        internal double Up { get; }
        internal PeerRole Role { get; }
        internal bool IsConnected { get; }
        internal bool IsActive { get; }
    }
}
