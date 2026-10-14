using System.Net;
using System.Text.Json;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Views;

public sealed partial class Inspector
{
    private (string TorrentId, TaskCompletionSource<JsonElement> Reply)? _peerRead;
    private (string Address, int Version)? _originKey;
    private Location? _origin;

    public bool IsGlobal
    {
        get
        {
            if (_section is not (InspectorSection.Peers or InspectorSection.Speed))
                return false;
            return _target is null && _owner.Selected.Count == 0;
        }
    }
    public bool HasContent => _target is not null || IsGlobal;

    // With no torrent selected, only Peers and Speed have content: those of
    // all torrents.
    internal bool CanShow(InspectorSection section) =>
        section is InspectorSection.Peers or InspectorSection.Speed
        || _target is not null
        || _owner.Selected.Count > 0;
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
                _origin = IPAddress.TryParse(key.PublicAddress, out var address)
                    ? _owner.Geolocation.Find(address) : null;
            }
            return _origin;
        }
    }

    private async Task<bool> ReadPeers(int context)
    {
        List<(string TorrentId, JsonElement Data)> rows = [];
        var global = IsGlobal;
        Torrent[] targets = global ? _owner.Torrents.Where(torrent => torrent.PeerCount > 0).ToArray() : [_target!];
        // Awaiting the fresh detail push leaves the command pipe free while
        // libtorrent answers, without overlapping Inspector demands.
        foreach (var torrent in targets)
        {
            if (context != _context || !_visible || !IsAvailable)
                return false;
            if (global && !_owner.Contains(torrent))
                continue;
            var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _peerRead = (torrent.TorrentId, pending);
            try
            {
                await _client.Read(Consumer.Inspector, "torrent", new
                {
                    torrent_id = torrent.TorrentId,
                    context,
                    view = "peers",
                });
                var reply = await pending.Task.WaitAsync(PipeClient.RequestTimeout);
                if (context != _context || !_visible || !IsAvailable)
                    return false;
                rows.AddRange(reply.GetProperty("peers").EnumerateArray()
                    .Select(data => (torrent.TorrentId, data)));
            }
            catch (CommandException error) when (global && error.Code == "torrent_removed")
            {
                // Removing one torrent does not discard the remaining torrents' peers.
            }
            finally
            {
                if (ReferenceEquals(_peerRead?.Reply, pending))
                    _peerRead = null;
            }
        }
        if (context != _context || !_visible || !IsAvailable)
            return false;
        var torrents = _owner.Torrents.Select(torrent => torrent.TorrentId).ToHashSet();
        var changed = ApplyPeers(rows.Where(row => torrents.Contains(row.TorrentId))) || _readFailure is not null;
        _readFailure = null;
        return changed;
    }

    private bool ApplyPeers(IEnumerable<(string TorrentId, JsonElement Data)> rows)
    {
        var previous = (Peers ?? []).ToDictionary(peer => (peer.TorrentId, peer.Endpoint));
        var next = rows.DistinctBy(row => (row.TorrentId, row.Data.GetProperty("endpoint").GetString()))
            .Select(row =>
            {
                var key = (row.TorrentId, row.Data.GetProperty("endpoint").GetString()!);
                if (!previous.TryGetValue(key, out var peer))
                    return new Peer(Text, _owner.Geolocation, row.Data, row.TorrentId);
                peer.Update(row.Data);
                return peer;
            }).ToArray();
        if (Peers is not null && Peers.SequenceEqual(next))
        {
            RowsUpdated?.Invoke(this, InspectorSection.Peers);
            return false;
        }
        Peers = next;
        return true;
    }
}
