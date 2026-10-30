using System.Net;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Engine;
using Syno.TinyTorrent.Torrents;

namespace Syno.TinyTorrent.Shell;

public sealed partial class Inspector
{
    private int _locationVersion;
    private readonly HashSet<Peer> _located = [];
    private bool _locating;
    private readonly List<(string TorrentId, IReadOnlyList<Peer> Peers)> _peerCache = [];
    private (string Address, int Version)? _originKey;
    private Location? _origin;
    private IReadOnlyList<Peer>? _peers;
    private PeerPlace? _place;

    internal MapModel Map => field ??= new MapModel(this);

    internal void UpdateMap(bool cities)
    {
        // A chosen place belongs to one map detail.
        if (Map.Cities != cities)
            Place = null;
        Map.Update(IsAvailable ? Peers : null, Origin, Text, cities);
    }

    /// <summary>
    /// The peer map place whose peers the table shows, in every connection
    /// state, or null for every peer.
    /// </summary>
    internal PeerPlace? Place
    {
        get => _place;
        set
        {
            if (_place == value)
                return;
            _place = value;
            Filter();
            Map.RefreshPlace();
            Refresh();
        }
    }

    /// <summary>The table's rows: <see cref="Peers"/>, or those at <see cref="Place"/>.</summary>
    public IReadOnlyList<Peer>? ShownPeers { get; private set; }

    // Rows that did not change keep their list, so the table keeps its scroll
    // position and selection. Returns whether the rows changed.
    private bool Filter()
    {
        if (_place is not { } place || _peers is null)
        {
            var changed = !ReferenceEquals(ShownPeers, _peers);
            ShownPeers = _peers;
            return changed;
        }
        var shown = _peers.Where(peer => place.Contains(peer.Location)).ToArray();
        if (ShownPeers is not null && shown.SequenceEqual(ShownPeers))
            return false;
        ShownPeers = shown;
        return true;
    }

    internal bool HasCities => _owner.Geolocation.Installed is not null;

    internal bool ShowsAllPeers { get; private set; }
    internal IReadOnlyList<Torrent>? PeerSelection { get; private set; }
    internal bool HasPeerScope => ShowsAllPeers || PeerSelection is not null;

    private IEnumerable<Torrent> Scope =>
        ShowsAllPeers ? _owner.Torrents
        : PeerSelection is { } selected ? selected.Where(_owner.Contains)
        : _target is not null ? [_target]
        : _owner.Selected.Count == 0 ? _owner.Torrents
        : _owner.Selected;

    public string DownloadText =>
        IsAvailable ? Text.Rate(Scope.Sum(torrent => torrent.DownloadRate))
        : "—";

    public string UploadText =>
        IsAvailable ? Text.Rate(Scope.Sum(torrent => torrent.UploadRate))
        : "—";

    public bool IsGlobal
    {
        get
        {
            if (_section is not (InspectorSection.Peers or InspectorSection.Speed))
                return false;
            return ShowsAllPeers
                || PeerSelection is null && _target is null && _owner.Selected.Count == 0;
        }
    }
    public bool HasContent =>
        _target is not null || _section is InspectorSection.Peers or InspectorSection.Speed;

    internal bool CanShow(InspectorSection section) =>
        section is InspectorSection.Peers or InspectorSection.Speed || _target is not null;

    internal string PeerSummary =>
        IsVisible && _section == InspectorSection.Peers && HasContent && Peers is { } peers
            ? Text.Format(
                "peers",
                "summary",
                peers.Count,
                peers.Count(peer => peer.State != PeerState.Known),
                peers.Count(peer =>
                    peer.State == PeerState.Connected && peer.DownloadRate + peer.UploadRate > 0
                )
            )
            : string.Empty;

    internal Location? Origin
    {
        get
        {
            var key = (_owner.PublicAddress, _owner.Geolocation.Version);
            if (_originKey != key)
                _ = LocatePeers();
            return _originKey == key ? _origin : null;
        }
    }

    // IPEndPoint also parses a bare address, so the public address and a
    // peer's endpoint share this lookup.
    private Location? Locate(string address) =>
        IPEndPoint.TryParse(address, out var endpoint)
            ? _owner.Geolocation.Find(endpoint.Address)
            : null;

    private async Task LocatePeers()
    {
        if (_locating)
            return;
        _locating = true;
        try
        {
            while (
                IsVisible && _section == InspectorSection.Peers && IsAvailable && !_owner.IsClosing
            )
            {
                var key = (_owner.PublicAddress, _owner.Geolocation.Version);
                if (_locationVersion != key.Version)
                {
                    _locationVersion = key.Version;
                    _located.Clear();
                    _originKey = null;
                }
                _located.IntersectWith(Peers ?? []);
                var peers = (Peers ?? []).Where(peer => !_located.Contains(peer)).ToArray();
                var cancellation = _reading.Token;
                var origin = _origin;
                var needsOrigin = _originKey != key;
                if (peers.Length == 0 && !needsOrigin)
                    return;
                var locations = await Task.Run(() =>
                    (
                        Peers: peers
                            .Select(peer =>
                            {
                                cancellation.ThrowIfCancellationRequested();
                                return (Peer: peer, Location: Locate(peer.Endpoint));
                            })
                            .ToArray(),
                        Origin: needsOrigin ? Locate(key.PublicAddress) : origin
                    ), cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (_owner.Geolocation.Version != key.Version)
                    continue;
                if (
                    !IsVisible
                    || _section != InspectorSection.Peers
                    || !IsAvailable
                    || _owner.IsClosing
                )
                    return;
                var current = (Peers ?? []).ToHashSet();
                var changed = false;
                var started = Stopwatch.GetTimestamp();
                for (var index = 0; index < locations.Peers.Length; index++)
                {
                    if (Stopwatch.GetElapsedTime(started) >= UpdateBudget)
                    {
                        await Yield();
                        started = Stopwatch.GetTimestamp();
                        cancellation.ThrowIfCancellationRequested();
                        if (!IsVisible || _section != InspectorSection.Peers || !IsAvailable || _owner.IsClosing)
                            return;
                        if (_owner.Geolocation.Version != key.Version)
                            break;
                        current = (Peers ?? []).ToHashSet();
                    }
                    var location = locations.Peers[index];
                    if (!current.Contains(location.Peer))
                        continue;
                    location.Peer.Location = location.Location;
                    _located.Add(location.Peer);
                    changed = true;
                }
                if (_owner.Geolocation.Version != key.Version)
                    continue;
                if (needsOrigin && _owner.PublicAddress == key.PublicAddress)
                {
                    _originKey = key;
                    _origin = locations.Origin;
                    changed = true;
                }
                if (changed)
                {
                    RowsUpdated?.Invoke(this, InspectorSection.Peers);
                    // A peer located just now can belong to the chosen place.
                    if (Filter())
                        Refresh();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _locating = false;
            if (IsVisible && _section == InspectorSection.Peers && IsAvailable
                && !_owner.IsClosing && (Peers ?? []).Any(peer => !_located.Contains(peer)))
                _ = LocatePeers();
        }
    }

    private async Task<bool> ReadPeers(int context)
    {
        var aggregate = _target is null;
        Torrent[] targets = aggregate
            ? Scope.Where(torrent => torrent.PeerCount > 0 || torrent.KnownPeerCount > 0).ToArray()
            : [_target!];
        var ids = targets.Select(torrent => torrent.TorrentId).ToHashSet();
        var present = _owner.Torrents.Select(torrent => torrent.TorrentId).ToHashSet();
        _peerCache.RemoveAll(entry => !present.Contains(entry.TorrentId));
        if (Peers is null && _peerCache.Any(entry => ids.Contains(entry.TorrentId)))
        {
            Peers = targets
                .SelectMany(torrent =>
                    _peerCache.Find(entry => entry.TorrentId == torrent.TorrentId).Peers ?? []
                )
                .ToArray();
            IsReplacing = false;
            Refresh();
        }
        if (Peers is { } previous && previous.Any(peer => !ids.Contains(peer.TorrentId)))
        {
            Peers = previous.Where(peer => ids.Contains(peer.TorrentId)).ToArray();
            Refresh();
        }
        // Returning to Peers resumes lookups abandoned while it was hidden.
        _ = LocatePeers();
        var groups = (Peers ?? [])
            .GroupBy(peer => peer.TorrentId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        // One torrent at a time, so Inspector demands never overlap.
        foreach (var torrent in targets)
        {
            if (!IsCurrent(context))
            {
                return false;
            }
            if (aggregate && !_owner.Contains(torrent))
            {
                continue;
            }
            try
            {
                await ReadDetail(
                    torrent,
                    InspectorSection.Peers,
                    context,
                    async detail =>
                    {
                        if (detail.GetProperty("peers").ValueKind == JsonValueKind.Null)
                            return;
                        var changed = await ApplyPeers(
                            torrent.TorrentId,
                            detail.GetProperty("peers"),
                            groups,
                            context
                        );
                        if (!IsCurrent(context))
                            return;
                        changed |= _readFailure is not null;
                        _readFailure = null;
                        if (changed)
                            Refresh();
                    }
                );
            }
            catch (CommandException error) when (aggregate && error.Code == "torrent_removed")
            {
                // Removing one torrent does not discard the remaining torrents' peers.
            }
        }
        if (!IsCurrent(context))
        {
            return false;
        }
        var changed = _readFailure is not null || Peers is null || IsReplacing;
        present = _owner.Torrents.Select(torrent => torrent.TorrentId).ToHashSet();
        var remaining = (Peers ?? []).Where(peer => present.Contains(peer.TorrentId)).ToArray();
        changed |= Peers is not null && remaining.Length != Peers.Count;
        if (changed)
            Peers = remaining;
        IsReplacing = false;
        _readFailure = null;
        return changed;
    }

    private async Task<bool> ApplyPeers(string torrentId, JsonElement rows, Dictionary<string, Peer[]> groups, int context)
    {
        var cancellation = _reading.Token;
        var group = groups.GetValueOrDefault(torrentId) ?? [];
        var previous = group.ToDictionary(peer => peer.Endpoint);
        var received = new List<Peer>();
        var next = (Peers ?? []).ToList();
        var published = false;
        await foreach (var batch in PreparePeers(torrentId, rows, cancellation))
        {
            await Yield();
            if (!IsCurrent(context))
                return false;
            var started = Stopwatch.GetTimestamp();
            var added = false;
            var changed = false;
            foreach (var parsed in batch)
            {
                if (Stopwatch.GetElapsedTime(started) >= UpdateBudget)
                {
                    await Yield();
                    if (!IsCurrent(context))
                        return false;
                    started = Stopwatch.GetTimestamp();
                }
                if (previous.TryGetValue(parsed.Endpoint, out var peer))
                {
                    changed |= peer.Update(parsed);
                    received.Add(peer);
                }
                else
                {
                    received.Add(parsed);
                    next.Add(parsed);
                    added = true;
                }
            }
            if (added)
            {
                Peers = next.ToArray();
                IsReplacing = false;
                published = true;
                Refresh();
                _ = LocatePeers();
            }
            else if (changed)
                RowsUpdated?.Invoke(this, InspectorSection.Peers);
        }
        if (!IsCurrent(context))
            return false;
        var complete = received.ToArray();
        groups[torrentId] = complete;
        _peerCache.RemoveAll(entry => entry.TorrentId == torrentId);
        _peerCache.Insert(0, (torrentId, complete));
        // Keep recent selections instant without retaining every torrent's peers.
        if (_peerCache.Count > 8)
            _peerCache.RemoveAt(_peerCache.Count - 1);
        if (Peers is not null && group.SequenceEqual(complete))
            return published;
        var insertion = next.FindIndex(peer => peer.TorrentId == torrentId);
        next.RemoveAll(peer => peer.TorrentId == torrentId);
        next.InsertRange(insertion < 0 ? next.Count : insertion, complete);
        if (Peers is not null && Peers.SequenceEqual(next))
            return published;
        Peers = next;
        IsReplacing = false;
        _ = LocatePeers();
        return true;
    }

    private async IAsyncEnumerable<Peer[]> PreparePeers(
        string torrentId, JsonElement rows, [EnumeratorCancellation] CancellationToken cancellation)
    {
        var text = Text;
        var endpoints = new HashSet<string>();
        var source = rows.EnumerateArray();
        var complete = false;
        while (!complete)
        {
            var batch = await Task.Run(() =>
            {
                var peers = new List<Peer>();
                var started = Stopwatch.GetTimestamp();
                do
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!source.MoveNext())
                    {
                        complete = true;
                        break;
                    }
                    var data = source.Current;
                    var endpoint = data.GetProperty("endpoint").GetString()!;
                    if (endpoints.Add(endpoint))
                        peers.Add(new Peer(text, torrentId, endpoint, null, data));
                } while (Stopwatch.GetElapsedTime(started) < UpdateBudget);
                return peers.ToArray();
            }, cancellation).ConfigureAwait(false);
            if (batch.Length > 0)
                yield return batch;
        }
    }
}
