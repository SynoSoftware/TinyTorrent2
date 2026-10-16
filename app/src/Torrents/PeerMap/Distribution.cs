using System.Numerics;
using Syno.TinyTorrent.Shared;

namespace Syno.TinyTorrent.Torrents;

// Estimated distribution: where a country's peers without coordinates most
// likely are. Each peer is taken to be at a populated region with probability
// proportional to its population. The map shows the most probable placement
// of the country's active connections and divides their measured traffic
// among the regions it uses by population. Selected table rows are placed the
// same way, so they mark the regions where those peers most likely are.
public sealed partial class PeerMap
{
    // Bundled places closer than this, in DIPs on the fitted map, form one region.
    private const float Spacing = 25;

    /// <summary>
    /// A country's populated places, with places that would overlap on the
    /// fitted map merged into regions, most populous first.
    /// </summary>
    private Geography.Area[] Regions(Geography.Area[] areas)
    {
        List<Geography.Area> regions = [];
        List<Vector2> positions = [];
        foreach (var area in areas)
        {
            var position = Project(area.Point);
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
    private static int Reach(Geography.Area[] regions, int connections)
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

    /// <summary>The peers selected in the table; their places are marked.</summary>
    private void SelectPeers(IReadOnlyList<Peer> peers)
    {
        _selectedPeers = peers;
        RefreshMarks();
    }

    // How many destinations of each place the selected peers mark, most
    // populous first. Drawing reads it every frame, so it changes only with
    // the selection, the destinations or the map detail.
    private void RefreshMarks()
    {
        if (_selectedPeers.Count == 0)
        {
            if (_marks.Count > 0)
                _marks = [];
            return;
        }
        Dictionary<PeerPlace, int> counts = [];
        foreach (var peer in _selectedPeers)
        {
            if (peer.Location is { } location)
            {
                var place = PeerPlace.Of(location, _cities);
                counts[place] = counts.GetValueOrDefault(place) + 1;
            }
        }
        Dictionary<PeerPlace, int> marks = [];
        foreach (var destination in _destinations)
        {
            var group = destination.Group;
            if (!marks.ContainsKey(group.Place) && counts.TryGetValue(group.Place, out var count))
                marks[group.Place] = group.Regions.Length == 0 ? 1 : Reach(group.Regions, count);
        }
        _marks = marks;
    }

    private bool IsMarked(Destination destination) =>
        destination.Key == _selected
        || _marks.GetValueOrDefault(destination.Group.Place) > destination.Rank;
}
