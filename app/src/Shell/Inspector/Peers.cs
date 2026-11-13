using System.ComponentModel;
using System.Net;
using System.Text.Json;
using Syno.TinyTorrent.Settings;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Engine;
using Syno.TinyTorrent.Torrents;

namespace Syno.TinyTorrent.Shell;

public sealed partial class Inspector
{
#if CAPTURE
    internal Action? PreparingPeers { get; set; }
    internal Func<Torrent, Task<JsonDocument>>? CapturePeers { get; set; }
    internal Func<IPAddress, Location?>? CaptureLocation { get; set; }
    internal Location? CaptureOrigin { get; set; }

    /// <summary>Keeps <paramref name="peers"/> as a torrent's peers, as its latest reply would.</summary>
    internal void CaptureSwarm(string torrentId, Peer[] peers) =>
        _swarms[torrentId] = new(new Swarm(torrentId, peers), peers.Count(peer => peer.Capture().Values.IsConnected),
            peers.Count(peer => peer.Capture().Values.IsActive), ++_swarmReads,
            _owner.Geolocation.Version);

    /// <summary>
    /// Applies <paramref name="rows"/> as the reply of a torrent the view shows.
    /// A torrent the view does not show becomes its only torrent, as a fixture
    /// that replaces the view's peers expects.
    /// </summary>
    internal Task<bool> ApplyCapturedPeers(string torrentId, JsonElement rows)
    {
        var slot = Array.FindIndex(_viewSwarms, swarm => swarm?.TorrentId == torrentId);
        if (slot < 0)
        {
            _viewSwarms = [null];
            slot = 0;
        }
        return ApplyPeers([(slot, torrentId, rows)], _context);
    }
#endif
    private static readonly PropertyChangedEventArgs ShownPeersChanged = new(nameof(ShownPeers));
    private static readonly PropertyChangedEventArgs PeerSummaryChanged = new(nameof(PeerSummary));
    // The location data version last seen, so only new data reads the shown
    // peers again.
    private int _locationVersion;
    // The public address's lookup runs.
    private bool _locating;
    // Each torrent's peers as its latest reply left them, for the scope and
    // the last few torrents read outside it, so going back shows them at once.
    private readonly Dictionary<string, KeptSwarm> _swarms = new(StringComparer.Ordinal);
    private long _swarmReads;
    // The swarms Peers is joined from, one per torrent of the poll round.
    private Swarm?[] _viewSwarms = [];
    private (string Address, int Version)? _originKey;
    private Location? _origin;
    private IReadOnlyList<Peer>? _peers;
    private IReadOnlySet<PeerPlace>? _places;
    private int _filterVersion;
    // A join, a leave or a location changed since a finished filter last found
    // the chosen places' rows.
    private bool _rowsChanged;
    private TaskCompletionSource? _peersReady;
    private (int Count, int Connected, int Active) _peerCounts;
    // The view's peers the map was last given.
    private MapModel.Population? _mapPopulation;
    // The replies that changed the view since then, or null when the map must
    // group every peer again.
    private List<Swarm.Reply>? _mapReplies;
    // The subject whose map has shown a whole poll round.
    private object? _mapRoundSubject;
    // The subject's replies that changed peers since the table last took them.
    private readonly List<Swarm.Reply> _rowReplies = [];

    /// <summary>A torrent's peers and the counts its latest reply gave.</summary>
    /// <param name="Read">When the reply came, so the oldest ones outside the scope go first.</param>
    /// <param name="LocationVersion">The location data version its peers were looked up in.</param>
    private readonly record struct KeptSwarm(Swarm Swarm, int Connected, int Active, long Read, int LocationVersion);

    /// <summary>
    /// The person chose other peers: another torrent, scope or place. The table
    /// keeps its rows until the new ones come, and then shows them whole, even
    /// under the pointer.
    /// </summary>
    internal event EventHandler? PeersWithdrawn;

    // The table withdrew its rows and takes only a new list as the person's
    // change, so the next rows reach it as a new list even when they are the
    // same peers.
    private bool _withdrawn;

    private void WithdrawPeers()
    {
        _withdrawn = true;
        PeersWithdrawn?.Invoke(this, EventArgs.Empty);
    }

    internal MapModel Map => field ??= new MapModel(this);

    /// <summary>Changes when a peer's location does, the one peer fact the map's selection marks read.</summary>
    internal int PeerVersion { get; private set; }

    internal void UpdateMap(bool cities)
    {
        var origin = Origin;
        // Without the engine, or before the map has this subject's peers, it
        // shows none, and the peers it is given next are grouped again. A
        // replacement whose rows are unread keeps the last population.
        if (!IsAvailable || !ReferenceEquals(_mapPopulation?.Subject, PeerSubject) && !KeepsLastMap)
        {
            _mapPopulation = null;
            _mapReplies = null;
        }
        Map.Update(
            _mapPopulation ?? new(PeerSubject, []),
            origin,
            Text,
            cities,
            _owner.Settings.Capacity is (var download, var upload)
                ? (download + upload) * ConnectionSetup.MbitPerSecond
                : null
        );
    }

    /// <summary>
    /// The peer map places whose peers the table shows, in every connection
    /// state: the places of the destination chosen on the map, kept while the
    /// map zooms and pans. Null for every peer; never empty.
    /// </summary>
    internal IReadOnlySet<PeerPlace>? Places
    {
        get => _places;
        set
        {
            if (value is { Count: 0 })
                value = null;
            if (_places == value || _places is not null && value is not null && _places.SetEquals(value))
                return;
            _places = value;
            WithdrawPeers();
            Map.RefreshPlaces();
            Filter();
        }
    }

    // Only the table reads the rows, so a change of rows alone does not
    // refresh the whole inspector and its bindings.
    private void NotifyShownPeers()
    {
        if (!_owner.IsClosed && IsVisible)
            PropertyChanged?.Invoke(this, ShownPeersChanged);
    }

    /// <summary>The table's rows: <see cref="Peers"/>, or those at <see cref="Places"/>.</summary>
    public IReadOnlyList<Peer>? ShownPeers { get; private set; }

    // While a search for the chosen places' rows runs, the filter it serves:
    // the one that started it, or a later one that waits for it to end. Null
    // when no search runs.
    private int? _finding;

    /// <summary>
    /// The chosen places' rows are being found. Until they are, <see cref="ShownPeers"/>
    /// holds the rows from before the person's change, or none after another
    /// torrent was chosen, and the table must not take them as that change.
    /// </summary>
    internal bool FindsShownPeers => _finding == _filterVersion;

    /// <summary>
    /// Takes the peers whose <paramref name="sorted"/> properties changed since
    /// the last call, so the table reads only their sort keys again; a peer can
    /// appear more than once. A peer that joined arrives with the list.
    /// </summary>
    internal IReadOnlyList<Peer> TakeChangedRows(Peer.Properties sorted)
    {
        List<Peer>? changed = null;
        if (sorted != Peer.Properties.None)
        {
            foreach (var reply in _rowReplies)
            {
                foreach (var (peer, change) in reply.Changes)
                {
                    if ((change.Properties & sorted) != 0)
                    {
                        (changed ??= []).Add(peer);
                    }
                }
            }
        }
        _rowReplies.Clear();
        if (changed is null)
        {
            return [];
        }
        return changed;
    }

    /// <summary>
    /// Starts a new subject of the peers. The table reads every sort key of a
    /// new subject's rows, so the changes it has not taken are dropped.
    /// </summary>
    private void RenewPeerSubject()
    {
        PeerSubject = new();
        _rowReplies.Clear();
    }

    internal Task PeersReady => _peersReady?.Task ?? Task.CompletedTask;

    /// <param name="rowsMayChange">
    /// False when only peers' values changed, so a choice keeps the rows it shows.
    /// </param>
    private async void Filter(bool rowsMayChange = true)
    {
        var version = ++_filterVersion;
        var subject = PeerSubject;
        bool IsCurrent() => version == _filterVersion && ReferenceEquals(subject, PeerSubject);
        if (_peersReady?.Task.IsCompleted != false)
            _peersReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _rowsChanged |= rowsMayChange;
        var findsRows = _rowsChanged;
        var peers = _peers;
        var places = _places;
        var previous = ShownPeers;
        var withdrawn = _withdrawn;
        if (places is null || peers is null)
        {
            ShownPeers = peers;
            _withdrawn = false;
            if (!ReferenceEquals(previous, peers))
                NotifyShownPeers();
        }
        var shown = places is null || peers is null ? peers : previous;
        var searched = false;
        // Only finding the chosen places' rows again passes over the peers.
        if (places is { } chosen && peers is { } all && (findsRows || previous is null))
        {
            // One search runs at a time. A call during it leaves the search to
            // the one that follows it, which reads the latest peers and places,
            // so a burst of replies searches the population once more, not
            // once per reply.
            var running = _finding is not null;
            _finding = version;
            if (running)
                return;
            shown = await Task.Run(() =>
            {
                // The places of one choice belong to one map detail.
                var cities = chosen.First().Cities;
                IReadOnlyList<Peer> found = all
                    .Where(peer => peer.Location is { } location && chosen.Contains(PeerPlace.Of(location, cities)))
                    .ToArray();
                return !withdrawn && previous is not null && previous.SequenceEqual(found) ? previous : found;
            });
            searched = true;
            var waiting = _finding != version;
            _finding = null;
            if (waiting)
            {
                Filter();
                return;
            }
        }
        if (!IsCurrent())
            return;
        // The table took no rows while they were found, so it is told when they
        // are, also when they stay the same.
        if (searched || !ReferenceEquals(ShownPeers, shown))
        {
            ShownPeers = shown;
            _withdrawn = false;
            NotifyShownPeers();
            if (!IsCurrent())
                return;
        }
        _rowsChanged = false;
        _peersReady!.TrySetResult();
    }

    // The summary's counts, summed over the view's swarms as their replies counted them.
    private void ShowCounts()
    {
        (int Count, int Connected, int Active) counts = (Peers?.Count ?? 0, 0, 0);
        foreach (var swarm in _viewSwarms)
        {
            if (swarm is not null && _swarms.TryGetValue(swarm.TorrentId, out var kept))
            {
                counts.Connected += kept.Connected;
                counts.Active += kept.Active;
            }
        }
        if (_peerCounts == counts)
        {
            return;
        }
        _peerCounts = counts;
        if (!_owner.IsClosed && IsVisible)
        {
            PropertyChanged?.Invoke(this, PeerSummaryChanged);
        }
    }

    /// <summary>
    /// Gives the map the view's peers and how they changed. The table takes
    /// each torrent's reply, but each picture of the map draws the whole
    /// scope, so once a poll round of the subject has finished, later rounds
    /// give the map their changes once, when they end. Until then each reply
    /// shows at once, so a new subject's map fills while its first round loads.
    /// </summary>
    private void ShowMap()
    {
        // A round's changes wait for its end; a new view, which the map groups
        // again, shows at once.
        if (ReferenceEquals(_mapRoundSubject, PeerSubject) && _mapReplies is not null)
            return;
        GiveMap();
    }

    // While a replacement's rows are unread, the map keeps its last picture,
    // as the rest of the inspector keeps its detail; an empty map would
    // release every arc only to build them again when the rows arrive. A
    // new scope of unread torrents shows an empty table at once, so the
    // test is the swarms, not the rows.
    private bool KeepsLastMap => IsReplacing && _viewSwarms.All(swarm => swarm is null);

    private void GiveMap()
    {
        if (KeepsLastMap)
            return;
        var previous = _mapPopulation;
        if (ReferenceEquals(previous?.Subject, PeerSubject))
        {
            if (_mapReplies is { Count: 0 })
                return;
        }
        else
        {
            _mapRoundSubject = null;
            _mapReplies = null;
        }
        _mapPopulation = new(PeerSubject, Peers ?? []) { Changes = _mapReplies, Follows = previous?.Number ?? 0 };
        _mapReplies = [];
        UpdateMap(Map.Cities);
    }

    /// <summary>Called when a poll round of the peers ends; gives the map the changes the round gathered.</summary>
    private void EndMapRound()
    {
        GiveMap();
        if (ReferenceEquals(_mapPopulation?.Subject, PeerSubject))
            _mapRoundSubject = PeerSubject;
    }

    // Shows a list whose difference from the last one the map's changes name.
    private void ShowPeers(IReadOnlyList<Peer>? peers)
    {
        _peers = peers;
        Filter();
        ShowMap();
    }

    internal bool HasCities =>
#if CAPTURE
        CaptureLocation is not null ||
#endif
        _owner.Geolocation.Download.Installed is not null;

    internal bool ShowsAllPeers { get; private set; }
    internal IReadOnlyList<Torrent>? PeerSelection { get; private set; }
    internal bool HasPeerScope => ShowsAllPeers || PeerSelection is not null;

    // The torrents the peer views are narrowed to, or null when they cover
    // every torrent: the person chose all, or nothing narrower is chosen.
    private IEnumerable<Torrent>? Narrowed =>
        ShowsAllPeers ? null
        : PeerSelection is { } selected ? selected.Where(_owner.Contains)
        : _target is not null ? [_target]
        : _owner.Selected.Count == 0 ? null
        : _owner.Selected;

    private IEnumerable<Torrent> Scope => Narrowed ?? _owner.Torrents;

    public string DownloadText =>
        OpensConnections ? Text.Rate(Scope.Sum(torrent => torrent.DownloadRate)) : Strings.NoValue;

    public string UploadText =>
        OpensConnections ? Text.Rate(Scope.Sum(torrent => torrent.UploadRate)) : Strings.NoValue;

    private bool OpensConnections => IsAvailable && Scope.Any(torrent => torrent.OpensConnections);

    public bool IsGlobal =>
        _section is (InspectorSection.Peers or InspectorSection.Speed) && Narrowed is null;
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
#if CAPTURE
            if (CaptureOrigin is { } origin)
                return origin;
#endif
            var key = (_owner.PublicAddress, _owner.Geolocation.Version);
            if (_originKey != key)
                _ = LocateOrigin();
            return _originKey == key ? _origin : null;
        }
    }

    // IPEndPoint also parses a bare address, such as the public address.
    private Location? Locate(string address) =>
        IPEndPoint.TryParse(address, out var endpoint)
            ? _owner.Geolocation.Find(endpoint.Address)
            : null;

    private bool CanLocate => IsVisible && _section == InspectorSection.Peers && IsAvailable && !_owner.IsClosing;

    // Looks up the public address, the map's origin, while Peers shows, and
    // again when the address or the location data changes. The worker that
    // reads a reply looks up its peers.
    private async Task LocateOrigin()
    {
        if (_locating)
            return;
        _locating = true;
        try
        {
            var key = (_owner.PublicAddress, _owner.Geolocation.Version);
            while (CanLocate && _originKey != key)
            {
                var address = key.PublicAddress;
                var origin = await Task.Run(() => Locate(address));
                if (key == (_owner.PublicAddress, _owner.Geolocation.Version))
                {
                    _originKey = key;
                    _origin = origin;
                    UpdateMap(Map.Cities);
                }
                key = (_owner.PublicAddress, _owner.Geolocation.Version);
            }
        }
        finally
        {
            _locating = false;
        }
    }

    // Returns whether the pane must refresh: the first rows, a replacement's
    // end or a failure cleared. Rows that change alone reach the table without it.
    private async Task<bool> ReadPeers(int context)
    {
        var targets = Subjects();
        KeepSwarms(targets);
        var swarms = new Swarm?[targets.Length];
        for (var index = 0; index < targets.Length; index++)
            swarms[index] = _swarms.TryGetValue(targets[index].TorrentId, out var kept) ? kept.Swarm : null;
        if (!_viewSwarms.SequenceEqual(swarms))
        {
            var first = Peers is null;
            if (!await ShowSwarms(swarms, context, release: !first))
                return false;
            // The first rows came from kept swarms.
            if (first && Peers is not null)
            {
                IsReplacing = false;
                Refresh();
            }
        }
        else if (!ReferenceEquals(_mapPopulation?.Subject, PeerSubject))
        {
            Filter();
            ShowMap();
        }
#if CAPTURE
        // The capture's fixture answers one torrent at a time, as its
        // measurements always have.
        if (CapturePeers is { } capture)
        {
            for (var slot = 0; slot < targets.Length; slot++)
            {
                if (!IsCurrent(context))
                    return false;
                // ApplyPeers awaits its worker, so nothing reads the rows after it.
                using var detail = await capture(targets[slot]);
                if (detail.RootElement.GetProperty("peers") is not { ValueKind: not JsonValueKind.Null } rows)
                    continue;
                var refresh = await ApplyPeers([(slot, targets[slot].TorrentId, rows)], context);
                if (!IsCurrent(context))
                    return false;
                refresh |= _readFailure is not null;
                _readFailure = null;
                if (refresh)
                    Refresh();
            }
        }
        else
#endif
            await Subscribe(context, targets, InspectorSection.Peers);
        if (!IsCurrent(context))
        {
            return false;
        }
        // A torrent removed meanwhile leaves the view when the next snapshot
        // takes it out of the scope, which subscribes again.
        var refreshes = _readFailure is not null || Peers is null || IsReplacing;
        IsReplacing = false;
        // A view of one torrent that could not be read keeps showing why.
        if (targets.Length == 1 && _failed.Contains(targets[0].TorrentId))
            return refreshes;
        // A scope whose torrents have no peers is read and empty.
        Peers ??= [];
        _readFailure = null;
        return refreshes;
    }

    // Keeps the swarms of the scope's torrents, and of the eight torrents read
    // most recently outside it that are still present.
    private void KeepSwarms(Torrent[] targets)
    {
        var scope = targets.Select(torrent => torrent.TorrentId).ToHashSet();
        var outside = _swarms
            .Where(entry => !scope.Contains(entry.Key))
            .OrderByDescending(entry => entry.Value.Read)
            .Select(entry => entry.Key)
            .ToArray();
        for (var index = 0; index < outside.Length; index++)
        {
            if (index >= 8 || _owner.Find(outside[index]) is null)
                _swarms.Remove(outside[index]);
        }
    }

    /// <summary>
    /// Shows the peers of <paramref name="swarms"/>, joined on a worker. Returns
    /// false when a newer read replaced this one meanwhile.
    /// </summary>
    /// <param name="release">The person changed the scope, so its rows change at once.</param>
    private async Task<bool> ShowSwarms(Swarm?[] swarms, int context, bool release)
    {
        if (Peers is null && swarms.All(swarm => swarm is null))
        {
            _viewSwarms = swarms;
            return true;
        }
        var joining = Task.Run(() => Swarm.Join(swarms), _reading.Token);
        await ((Task)joining).ConfigureAwait(
            ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        if (!IsCurrent(context) || joining.IsCanceled)
            return false;
        if (release)
            WithdrawPeers();
        _viewSwarms = swarms;
        Peers = await joining;
        ShowCounts();
        return true;
    }

    /// <summary>Forgets the shown peers; the swarms stay for a return to their torrents.</summary>
    private void ForgetView()
    {
        WithdrawPeers();
        _viewSwarms = [];
        Peers = null;
    }

    /// <summary>Forgets every torrent's peers, which another engine session does not have.</summary>
    private void ForgetSwarms()
    {
        _swarms.Clear();
        _rowReplies.Clear();
        ForgetView();
    }

    /// <summary>
    /// Forgets the swarms the view does not show. Their peers could be stale when
    /// the engine connects again; the shown ones are read again at once.
    /// </summary>
    private void ForgetHiddenSwarms()
    {
        var shown = _viewSwarms.OfType<Swarm>().Select(swarm => swarm.TorrentId).ToHashSet();
        foreach (var torrentId in _swarms.Keys.Where(torrentId => !shown.Contains(torrentId)).ToArray())
            _swarms.Remove(torrentId);
    }

    /// <summary>
    /// Reads each torrent's peers into its swarm, which sits at its slot of the
    /// view's torrents, in one pass on a worker that also looks up the peers'
    /// locations. Only the peers that changed raise their properties; a join or
    /// leave publishes the view's new list, joined once for the whole batch.
    /// Returns whether the pane must refresh: for the first rows or a
    /// replacement's end.
    /// </summary>
    private async Task<bool> ApplyPeers(
        IReadOnlyList<(int Slot, string TorrentId, JsonElement Rows)> batch,
        int context
    )
    {
        // A later read owns the view, which can be a different size.
        if (!IsCurrent(context))
            return false;
        var cancellation = _reading.Token;
        var text = Text;
        var version = _owner.Geolocation.Version;
        var previous = new KeptSwarm?[batch.Count];
        for (var index = 0; index < batch.Count; index++)
            previous[index] = _swarms.TryGetValue(batch[index].TorrentId, out var kept) ? kept : null;
        var view = _viewSwarms;
        Func<IPAddress, Location?> locate = _owner.Geolocation.Find;
#if CAPTURE
        locate = CaptureLocation ?? locate;
#endif
        var preparation = Task.Run(() =>
        {
#if CAPTURE
            PreparingPeers?.Invoke();
#endif
            var replies = new Swarm.Reply[batch.Count];
            Swarm?[]? swarms = null;
            for (var index = 0; index < batch.Count; index++)
            {
                var (slot, torrentId, rows) = batch[index];
                var kept = previous[index];
                var relocate = kept.HasValue && kept.Value.LocationVersion != version;
                replies[index] = Swarm.Read(torrentId, kept?.Swarm, rows, text, locate, relocate, cancellation);
                if (!ReferenceEquals(replies[index].Swarm, view[slot]))
                    (swarms ??= (Swarm?[])view.Clone())[slot] = replies[index].Swarm;
            }
            return (Replies: replies, Swarms: swarms, Peers: swarms is null ? null : Swarm.Join(swarms));
        }, cancellation);
        await ((Task)preparation).ConfigureAwait(
            ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        if (!IsCurrent(context) || preparation.IsCanceled)
            return false;
        var (replies, swarms, peers) = await preparation;
        var changed = false;
        var located = false;
        for (var index = 0; index < batch.Count; index++)
        {
            var reply = replies[index];
            foreach (var (peer, change) in reply.Changes)
            {
                peer.Apply(change);
                located |= (change.Properties & Peer.Located) != 0;
            }
            _swarms[batch[index].TorrentId] = new(reply.Swarm, reply.Connected, reply.Active, ++_swarmReads, version);
            if (reply.Changes.Count > 0)
            {
                _rowReplies.Add(reply);
                changed = true;
            }
            if (_mapReplies is not null && MapModel.Affects(reply))
            {
                _mapReplies.Add(reply);
            }
        }
        if (located)
        {
            PeerVersion++;
        }
        var refreshes = Peers is null || IsReplacing;
        if (swarms is not null)
        {
            _viewSwarms = swarms;
            ShowPeers(peers);
        }
        else if (changed)
        {
            // A peer's new location can move it into or out of the chosen place.
            Filter(rowsMayChange: located);
            ShowMap();
        }
        if (changed)
            RowsUpdated?.Invoke(this, InspectorSection.Peers);
        ShowCounts();
        IsReplacing = false;
        return refreshes;
    }
}
