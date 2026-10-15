using System.Net;
using System.Text.Json;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Views;

public sealed partial class Inspector
{
    private int _locationVersion;
    private (string Address, int Version)? _originKey;
    private Location? _origin;

    internal bool HasCities => _owner.Geolocation.Installed is not null;

    public bool IsGlobal
    {
        get
        {
            if (_section is not (InspectorSection.Peers or InspectorSection.Speed))
                return false;
            return _target is null && _owner.Selected.Count == 0;
        }
    }
    public bool HasContent => _target is not null || _section is InspectorSection.Peers or InspectorSection.Speed;

    internal bool CanShow(InspectorSection section) =>
        section is InspectorSection.Peers or InspectorSection.Speed
        || _target is not null;
    internal string PeerSummary => _visible && _section == InspectorSection.Peers && HasContent && Peers is { } peers
        ? Text.Format("peers", "summary", peers.Count,
            peers.Count(peer => peer.State == PeerState.Connected && peer.DownloadRate + peer.UploadRate > 0))
        : string.Empty;

    internal Location? Origin
    {
        get
        {
            var key = (_owner.PublicAddress, _owner.Geolocation.Version);
            if (_originKey != key)
            {
                _originKey = key;
                _origin = Locate(key.PublicAddress);
            }
            return _origin;
        }
    }

    // IPEndPoint also parses a bare address, so the public address and a
    // peer's endpoint share this lookup.
    private Location? Locate(string address) =>
        IPEndPoint.TryParse(address, out var endpoint) ? _owner.Geolocation.Find(endpoint.Address) : null;

    private async Task<bool> ReadPeers(int context)
    {
        List<(string TorrentId, JsonElement Data)> rows = [];
        var aggregate = _target is null;
        var scope = _owner.Selected.Count == 0 ? _owner.Torrents : _owner.Selected;
        Torrent[] targets = aggregate ? scope.Where(torrent => torrent.PeerCount > 0).ToArray() : [_target!];
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
                var reply = await ReadDetail(torrent, InspectorSection.Peers, context);
                if (reply is not { } peers || !IsCurrent(context))
                {
                    return false;
                }
                rows.AddRange(peers.GetProperty("peers").EnumerateArray()
                    .Select(data => (torrent.TorrentId, data)));
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
        var torrents = _owner.Torrents.Select(torrent => torrent.TorrentId).ToHashSet();
        var changed = ApplyPeers(rows.Where(row => torrents.Contains(row.TorrentId))) || _readFailure is not null;
        _readFailure = null;
        return changed;
    }

    private bool ApplyPeers(IEnumerable<(string TorrentId, JsonElement Data)> rows)
    {
        var previous = (Peers ?? []).ToDictionary(peer => (peer.TorrentId, peer.Endpoint));
        var previousOrigin = _origin;
        var changed = false;
        // A peer's address never changes, so a kept peer is looked up again
        // only when the city data changes.
        var relocate = _locationVersion != _owner.Geolocation.Version;
        _locationVersion = _owner.Geolocation.Version;
        var next = rows
            .Select(row => (row.TorrentId, Endpoint: row.Data.GetProperty("endpoint").GetString()!, row.Data))
            .DistinctBy(row => (row.TorrentId, row.Endpoint))
            .Select(row =>
            {
                if (!previous.TryGetValue((row.TorrentId, row.Endpoint), out var peer))
                {
                    return new Peer(Text, row.TorrentId, row.Endpoint, Locate(row.Endpoint), row.Data);
                }
                changed |= peer.Update(row.Data);
                if (relocate)
                {
                    peer.Location = Locate(peer.Endpoint);
                }
                return peer;
            }).ToArray();
        if (Peers is not null && Peers.SequenceEqual(next))
        {
            if (changed || relocate || Origin != previousOrigin)
                RowsUpdated?.Invoke(this, InspectorSection.Peers);
            return false;
        }
        Peers = next;
        return true;
    }
}
