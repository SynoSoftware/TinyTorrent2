using System.Buffers;
using System.Net;
using System.Text.Json;
using Syno.TinyTorrent.Shared;

namespace Syno.TinyTorrent.Torrents;

/// <summary>
/// One torrent's peers, kept between replies with the endpoint index the next
/// reply is matched through. Peers keep the order they first appeared in: a
/// join appends and a leave removes, so a peer that connects or disconnects,
/// which moves it in the engine's list, does not move it here. A reply that
/// adds or removes no peer keeps the swarm; any other reply makes a new one, so
/// a worker that still reads this one never sees it change.
/// </summary>
internal sealed class Swarm
{
    private readonly Peer[] _peers;
    private readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _positions;

    internal Swarm(string torrentId, Peer[] peers)
    {
        TorrentId = torrentId;
        _peers = peers;
        var positions = new Dictionary<string, int>(peers.Length, StringComparer.Ordinal);
        for (var index = 0; index < peers.Length; index++)
        {
            positions.Add(peers[index].Endpoint, index);
        }
        _positions = positions.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    internal string TorrentId { get; }
    internal IReadOnlyList<Peer> Peers => _peers;

    /// <summary>
    /// The peers of <paramref name="swarms"/> in their order, skipping torrents
    /// not read yet: one swarm's own list, or one list of their exact size.
    /// </summary>
    internal static IReadOnlyList<Peer> Join(IReadOnlyList<Swarm?> swarms)
    {
        Swarm? only = null;
        var parts = 0;
        var count = 0;
        foreach (var swarm in swarms)
        {
            if (swarm is not null)
            {
                only = swarm;
                parts++;
                count += swarm._peers.Length;
            }
        }
        if (parts == 1)
        {
            return only!._peers;
        }
        var joined = new Peer[count];
        var at = 0;
        foreach (var swarm in swarms)
        {
            if (swarm is not null)
            {
                swarm._peers.CopyTo(joined, at);
                at += swarm._peers.Length;
            }
        }
        return joined;
    }

    /// <summary>What one reply did to a swarm.</summary>
    /// <param name="Connected">The peers in any state but known.</param>
    /// <param name="Active">The connected peers that move data.</param>
    internal sealed record Reply(
        Swarm Swarm,
        IReadOnlyList<(Peer Peer, Peer.Change Change)> Changes,
        IReadOnlyList<Peer> Joined,
        IReadOnlyList<Peer> Left,
        int Connected,
        int Active
    );

    /// <summary>
    /// Reads a torrent's peers array from an engine reply against the swarm its
    /// previous reply left, on a worker. A peer already known keeps its object
    /// and gets a change only when its values or location differ; a new
    /// endpoint gets a new peer, located by <paramref name="locate"/>. A peer
    /// listed twice is read once. Peers whose values did not change cost no
    /// allocation, and each list of the reply is made once, at its size.
    /// </summary>
    /// <param name="relocate">
    /// The location data changed since the known peers were looked up, so each
    /// listed one is looked up again.
    /// </param>
    internal static Reply Read(
        string torrentId,
        Swarm? previous,
        JsonElement peers,
        Strings text,
        Func<IPAddress, Location?> locate,
        bool relocate,
        CancellationToken cancellation
    )
    {
        var known = previous?._peers ?? [];
        var count = peers.GetArrayLength();
        var seen = ArrayPool<bool>.Shared.Rent(known.Length);
        // Only a listed known peer can change and only a listed new one can
        // join, which bounds both buffers.
        var changeBuffer = ArrayPool<(Peer, Peer.Change)>.Shared.Rent(Math.Min(count, known.Length));
        var joinBuffer = ArrayPool<Peer>.Shared.Rent(count);
        var endpointBuffer = ArrayPool<char>.Shared.Rent(Peer.TextLength);
        var clientBuffer = ArrayPool<char>.Shared.Rent(Peer.TextLength);
        var changeCount = 0;
        var joinCount = 0;
        try
        {
            Array.Clear(seen, 0, known.Length);
            HashSet<string>? added = null;
            var listed = 0;
            var connected = 0;
            var active = 0;
            foreach (var row in peers.EnumerateArray())
            {
                cancellation.ThrowIfCancellationRequested();
                var values = Peer.Read(row, endpointBuffer, clientBuffer, out var endpoint, out var client);
                if (endpoint.IsEmpty)
                {
                    continue;
                }
                if (previous is not null && previous._positions.TryGetValue(endpoint, out var position))
                {
                    if (seen[position])
                    {
                        continue;
                    }
                    seen[position] = true;
                    listed++;
                    var peer = known[position];
                    var record = peer.Capture();
                    var compared = Peer.Compare(record, values, client);
                    var changed = compared;
                    var location = peer.Location;
                    if (relocate)
                    {
                        var found = IPEndPoint.TryParse(peer.Endpoint, out var parsed) ? locate(parsed.Address) : null;
                        if (found != location)
                        {
                            location = found;
                            changed |= Peer.Located;
                        }
                    }
                    if (changed != Peer.Properties.None)
                    {
                        var next = compared == Peer.Properties.None ? record : Peer.Make(values, client);
                        changeBuffer[changeCount++] = (peer, new(next, location, changed));
                    }
                }
                else
                {
                    var address = endpoint.ToString();
                    added ??= new(Math.Max(count - known.Length, 0), StringComparer.Ordinal);
                    if (!added.Add(address))
                    {
                        continue;
                    }
                    // The peer and its lookup share one parse of the endpoint.
                    var parsed = IPEndPoint.TryParse(endpoint, out var found) ? found : null;
                    joinBuffer[joinCount++] = new Peer(text, torrentId, address, parsed,
                        parsed is null ? null : locate(parsed.Address), Peer.Make(values, client));
                }
                if (values.IsConnected)
                {
                    connected++;
                }
                if (values.IsActive)
                {
                    active++;
                }
            }
            var changes = changeBuffer[..changeCount];
            if (previous is not null && joinCount == 0 && listed == known.Length)
            {
                return new(previous, changes, [], [], connected, active);
            }
            var left = new Peer[known.Length - listed];
            var kept = new Peer[listed + joinCount];
            var at = 0;
            var gone = 0;
            for (var index = 0; index < known.Length; index++)
            {
                if (seen[index])
                {
                    kept[at++] = known[index];
                }
                else
                {
                    left[gone++] = known[index];
                }
            }
            Array.Copy(joinBuffer, 0, kept, at, joinCount);
            return new(
                new Swarm(torrentId, kept),
                changes,
                new ArraySegment<Peer>(kept, at, joinCount),
                left,
                connected,
                active
            );
        }
        finally
        {
            // The pooled buffers hold peers, which must not outlive the reply.
            Array.Clear(changeBuffer, 0, changeCount);
            Array.Clear(joinBuffer, 0, joinCount);
            ArrayPool<bool>.Shared.Return(seen);
            ArrayPool<(Peer, Peer.Change)>.Shared.Return(changeBuffer);
            ArrayPool<Peer>.Shared.Return(joinBuffer);
            ArrayPool<char>.Shared.Return(endpointBuffer);
            ArrayPool<char>.Shared.Return(clientBuffer);
        }
    }
}
