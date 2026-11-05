using System.ComponentModel;
using System.Net;
using System.Text.Json;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Engine;
using Syno.TinyTorrent.Torrents;

namespace Syno.TinyTorrent.Shell;

public sealed partial class Inspector
{
#if CAPTURE
    internal Action? PreparingPeers { get; set; }
#endif
    private int _locationVersion;
    private HashSet<Peer> _located = [];
    private bool _locating;
    private readonly List<(string TorrentId, IReadOnlyList<Peer> Peers)> _peerCache = [];
    private (string Address, int Version)? _originKey;
    private Location? _origin;
    private IReadOnlyList<Peer>? _peers;
    private PeerPlace? _place;
    private int _filterVersion;
    private TaskCompletionSource? _peersReady;
    private IReadOnlyList<Peer>? _releaseBaseline;
    private (int Connected, int Active) _peerCounts;

    internal event EventHandler<IReadOnlyList<Peer>>? PeersReleased;

    internal MapModel Map => field ??= new MapModel(this);

    internal void UpdateMap(bool cities)
    {
        // Decimal Mbit/s: 125,000 bytes per second each.
        Map.Update(
            IsAvailable ? Peers : null,
            Origin,
            Text,
            cities,
            _owner.Settings.Capacity is (var download, var upload) ? (download + upload) * 125_000 : null
        );
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
            _releaseBaseline ??= ShownPeers ?? [];
            Map.RefreshPlace();
            Filter();
        }
    }

    // Only the table reads the rows, so a change of rows alone does not
    // refresh the whole inspector and its bindings.
    private void NotifyShownPeers()
    {
        if (!_owner.IsClosed && IsVisible)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShownPeers)));
    }

    /// <summary>The table's rows: <see cref="Peers"/>, or those at <see cref="Place"/>.</summary>
    public IReadOnlyList<Peer>? ShownPeers { get; private set; }
    internal Task PeersReady => _peersReady?.Task ?? Task.CompletedTask;

    private sealed record PeerRows(IReadOnlyList<Peer>? Shown, IReadOnlyList<Peer> Released, int Connected, int Active);

    private async void Filter()
    {
        var version = ++_filterVersion;
        if (_peersReady?.Task.IsCompleted != false)
            _peersReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var peers = _peers;
        var place = _place;
        var previous = ShownPeers;
        if (peers is null)
            _releaseBaseline ??= previous ?? [];
        var release = _releaseBaseline;
        if (place is null || peers is null)
        {
            ShownPeers = peers;
            if (!ReferenceEquals(previous, peers))
                NotifyShownPeers();
        }
        var captured = (peers ?? []).Select(peer => (Peer: peer, peer.Location, Record: peer.Capture())).ToArray();
        var prepared = await Task.Run(() =>
        {
            IReadOnlyList<Peer>? shown = place is null || peers is null ? peers
                : captured.Where(row => place.Value.Contains(row.Location)).Select(row => row.Peer).ToArray();
            if (place is not null && previous is not null && shown is not null && previous.SequenceEqual(shown))
                shown = previous;
            var released = release is not null
                ? release.Except(shown ?? []).Concat((shown ?? []).Except(release)).ToArray() : [];
            return new PeerRows(shown, released,
                captured.Count(row => row.Record.State != PeerState.Known),
                captured.Count(row => row.Record.State == PeerState.Connected && row.Record.DownloadRate + row.Record.UploadRate > 0));
        });
        if (version != _filterVersion)
            return;
        _releaseBaseline = null;
        if (prepared.Released.Count > 0)
            PeersReleased?.Invoke(this, prepared.Released);
        if (!ReferenceEquals(ShownPeers, prepared.Shown))
        {
            ShownPeers = prepared.Shown;
            NotifyShownPeers();
        }
        var counts = (prepared.Connected, prepared.Active);
        if (_peerCounts != counts)
        {
            _peerCounts = counts;
            if (!_owner.IsClosed && IsVisible)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PeerSummary)));
        }
        _peersReady!.TrySetResult();
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
        OpensConnections ? Text.Rate(Scope.Sum(torrent => torrent.DownloadRate)) : "—";

    public string UploadText =>
        OpensConnections ? Text.Rate(Scope.Sum(torrent => torrent.UploadRate)) : "—";

    private bool OpensConnections => IsAvailable && Scope.Any(torrent => torrent.OpensConnections);

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
                _peerCounts.Connected,
                _peerCounts.Active
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

    private sealed record PeerLocations(IReadOnlyList<(Peer Peer, Location? Location)> Locations,
        HashSet<Peer> Located, Location? Origin, bool Missing = false);

    private bool CanLocate => IsVisible && _section == InspectorSection.Peers && IsAvailable && !_owner.IsClosing;

    private async Task LocatePeers()
    {
        if (_locating)
            return;
        _locating = true;
        var restart = false;
        try
        {
            while (CanLocate)
            {
                var key = (_owner.PublicAddress, _owner.Geolocation.Version);
                if (_locationVersion != key.Version)
                {
                    _locationVersion = key.Version;
                    _located = [];
                    _originKey = null;
                }
                var source = Peers;
                var located = _located;
                var cancellation = _reading.Token;
                var origin = _origin;
                var needsOrigin = _originKey != key;
                var preparation = Task.Run(() =>
                {
                    var members = (source ?? []).ToHashSet();
                    var retained = located.Where(members.Contains).ToHashSet();
                    List<(Peer, Location?)> locations = [];
                    foreach (var peer in source ?? [])
                    {
                        if (retained.Contains(peer))
                            continue;
                        cancellation.ThrowIfCancellationRequested();
                        locations.Add((peer, Locate(peer.Endpoint)));
                        retained.Add(peer);
                    }
                    return new PeerLocations(locations, retained, needsOrigin ? Locate(key.PublicAddress) : origin);
                }, cancellation);
                await ((Task)preparation).ConfigureAwait(
                    ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
                if (preparation.IsCanceled || cancellation.IsCancellationRequested)
                {
                    restart = true;
                    return;
                }
                var prepared = await preparation;
                while (!ReferenceEquals(source, Peers) && CanLocate && _owner.Geolocation.Version == key.Version)
                {
                    source = Peers;
                    var previous = prepared;
                    preparation = Task.Run(() =>
                    {
                        var members = (source ?? []).ToHashSet();
                        var retained = previous.Located.Where(members.Contains).ToHashSet();
                        return previous with
                        {
                            Locations = previous.Locations.Where(location => members.Contains(location.Peer)).ToArray(),
                            Located = retained,
                            Missing = retained.Count < members.Count,
                        };
                    }, cancellation);
                    await ((Task)preparation).ConfigureAwait(
                        ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
                    if (preparation.IsCanceled || cancellation.IsCancellationRequested)
                    {
                        restart = true;
                        return;
                    }
                    prepared = await preparation;
                }
                if (!CanLocate)
                    return;
                if (_owner.Geolocation.Version != key.Version)
                    continue;
                _located = prepared.Located;
                foreach (var location in prepared.Locations)
                    location.Peer.Location = location.Location;
                var changed = prepared.Locations.Count > 0;
                if (needsOrigin && _owner.PublicAddress == key.PublicAddress)
                {
                    _originKey = key;
                    _origin = prepared.Origin;
                    changed = true;
                }
                if (changed)
                {
                    RowsUpdated?.Invoke(this, InspectorSection.Peers);
                    Filter();
                }
                if (!prepared.Missing && ReferenceEquals(source, Peers)
                    && _originKey == (_owner.PublicAddress, _owner.Geolocation.Version))
                    return;
            }
        }
        finally
        {
            _locating = false;
            if (restart && CanLocate)
                _ = LocatePeers();
        }
    }

    private sealed record PeerScope(IReadOnlyList<Peer>? Rows, IReadOnlySet<string> Present, bool Cached);

    private async Task<bool> ReadPeers(int context)
    {
        var aggregate = _target is null;
        Torrent[] targets = aggregate
            ? Scope.Where(torrent => torrent.PeerCount > 0 || torrent.KnownPeerCount > 0).ToArray()
            : [_target!];
        var cancellation = _reading.Token;
        var current = Peers;
        var cache = _peerCache.ToArray();
        var present = _owner.Torrents.Select(torrent => torrent.TorrentId).ToArray();
        var preparation = Task.Run(() =>
        {
            var ids = targets.Select(torrent => torrent.TorrentId).ToHashSet();
            var alive = present.ToHashSet();
            var cached = current is null && cache.Any(entry => ids.Contains(entry.TorrentId));
            var shown = cached ? targets.SelectMany(torrent =>
                cache.FirstOrDefault(entry => entry.TorrentId == torrent.TorrentId).Peers ?? []).ToArray() : current;
            if (shown is not null && shown.Any(peer => !ids.Contains(peer.TorrentId)))
                shown = shown.Where(peer => ids.Contains(peer.TorrentId)).ToArray();
            return new PeerScope(shown, alive, cached);
        }, cancellation);
        await ((Task)preparation).ConfigureAwait(
            ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        if (!IsCurrent(context) || preparation.IsCanceled)
            return false;
        var prepared = await preparation;
        _peerCache.RemoveAll(entry => !prepared.Present.Contains(entry.TorrentId));
        if (!ReferenceEquals(current, prepared.Rows))
        {
            Peers = prepared.Rows;
            if (prepared.Cached)
                IsReplacing = false;
            Refresh();
        }
        // Returning to Peers resumes lookups abandoned while it was hidden.
        _ = LocatePeers();
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
        current = Peers;
        present = _owner.Torrents.Select(torrent => torrent.TorrentId).ToArray();
        var pruning = Task.Run(() =>
        {
            var alive = present.ToHashSet();
            return (current ?? []).Where(peer => alive.Contains(peer.TorrentId)).ToArray();
        }, cancellation);
        await ((Task)pruning).ConfigureAwait(
            ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        if (!IsCurrent(context) || pruning.IsCanceled)
            return false;
        var remaining = await pruning;
        var changed = _readFailure is not null || Peers is null || IsReplacing;
        changed |= Peers is not null && remaining.Length != Peers.Count;
        if (changed)
            Peers = remaining;
        IsReplacing = false;
        _readFailure = null;
        return changed;
    }

    private sealed record PeerUpdate(Peer[] Received, IReadOnlyList<Peer> Rows,
        IReadOnlyList<(Peer Peer, Peer.Change Change)> Changes);

    private async Task<bool> ApplyPeers(string torrentId, JsonElement rows, int context)
    {
        var cancellation = _reading.Token;
        var text = Text;
        var current = Peers ?? [];
        var captured = current.Select(peer => (Peer: peer, Record: peer.Capture())).ToArray();
        var preparation = Task.Run(() =>
        {
#if CAPTURE
            PreparingPeers?.Invoke();
#endif
            var previous = captured.Where(row => row.Peer.TorrentId == torrentId)
                .ToDictionary(row => row.Peer.Endpoint);
            HashSet<string> endpoints = [];
            List<Peer> received = [];
            List<(Peer, Peer.Change)> changes = [];
            foreach (var data in rows.EnumerateArray())
            {
                cancellation.ThrowIfCancellationRequested();
                var endpoint = data.GetProperty("endpoint").GetString()!;
                if (!endpoints.Add(endpoint))
                    continue;
                var parsed = Peer.Record.Parse(data);
                if (previous.TryGetValue(endpoint, out var before))
                {
                    if (Peer.Prepare(before.Record, parsed) is { } change)
                        changes.Add((before.Peer, change));
                    received.Add(before.Peer);
                }
                else
                    received.Add(new Peer(text, torrentId, endpoint, null, parsed));
            }
            var complete = received.ToArray();
            IReadOnlyList<Peer> next = current;
            if (!captured.Where(row => row.Peer.TorrentId == torrentId).Select(row => row.Peer).SequenceEqual(complete))
            {
                var merged = current.ToList();
                var insertion = merged.FindIndex(peer => peer.TorrentId == torrentId);
                merged.RemoveAll(peer => peer.TorrentId == torrentId);
                merged.InsertRange(insertion < 0 ? merged.Count : insertion, complete);
                next = merged.ToArray();
            }
            return new PeerUpdate(complete, next, changes);
        }, cancellation);
        await ((Task)preparation).ConfigureAwait(
            ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        if (!IsCurrent(context) || preparation.IsCanceled)
            return false;
        var prepared = await preparation;
        foreach (var (peer, change) in prepared.Changes)
            peer.Apply(change);
        _peerCache.RemoveAll(entry => entry.TorrentId == torrentId);
        _peerCache.Insert(0, (torrentId, prepared.Received));
        // Keep recent selections instant without retaining every torrent's peers.
        if (_peerCache.Count > 8)
            _peerCache.RemoveAt(_peerCache.Count - 1);
        var changed = !ReferenceEquals(current, prepared.Rows) || Peers is null;
        if (changed)
            Peers = prepared.Rows;
        else if (prepared.Changes.Count > 0)
        {
            Filter();
            RowsUpdated?.Invoke(this, InspectorSection.Peers);
        }
        IsReplacing = false;
        _ = LocatePeers();
        return changed;
    }
}
