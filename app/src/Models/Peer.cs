using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Models;

public sealed class Peer : INotifyPropertyChanged
{
    private readonly Strings _text;
    private PeerTransport _transport;
    private PeerEncryption _encryption;
    private PeerSource _sources;
    private bool _interested;
    private bool _choked;
    private bool _remoteInterested;
    private bool _remoteChoked;
    private TransferWait _readWait;
    private TransferWait _writeWait;

    public string Endpoint { get; }
    internal string TorrentId { get; }
    internal string Key => TorrentId + "/" + Endpoint;
    public string Client { get; private set; } = string.Empty;
    public bool Incoming { get; private set; }
    public PeerState State { get; private set; }
    public double Progress { get; private set; }
    public double DownloadRate { get; private set; }
    public double UploadRate { get; private set; }
    public long Downloaded { get; private set; }
    public long Uploaded { get; private set; }
    public string ProgressText => Progress.ToString("P1", CultureInfo.CurrentCulture);
    public string DownloadText => _text.Format("units", "rate", _text.Bytes(DownloadRate));
    public string UploadText => _text.Format("units", "rate", _text.Bytes(UploadRate));
    public string DownloadedText => _text.Bytes(Downloaded);
    public string UploadedText => _text.Bytes(Uploaded);
    public string ClientText => string.IsNullOrEmpty(Client) ? "—" : Client;

    /// <summary>The ISO country code that names the peer's flag, or empty without a location.</summary>
    public string CountryCode => Location?.Code ?? string.Empty;
    public string CountryText => Location?.CountryName(_text.Language) ?? "—";
    public string CityText => Location?.City?.Name(_text.Language) ?? "—";
    public string LocationText => Location?.City?.Name(_text.Language) is { } city
        ? _text.Format("peers", "location_city", city, CountryText) : CountryText;

    internal Location? Location
    {
        get;
        set
        {
            field = value;
            RefreshText();
        }
    }

    public string StateText => _text.Get("peers", State.ToString().ToLowerInvariant());
    public string StateGlyph => State switch
    {
        PeerState.Connecting => Syno.Lucide.Plug,
        PeerState.Handshake => Syno.Lucide.Handshake,
        PeerState.Connected => Syno.Lucide.CircleCheck,
        _ => Syno.Lucide.CircleQuestionMark,
    };
    public string StateHint => _text.Format("peers", "state_hint", StateExplanation, SourcesText);
    public string ClientHint => string.IsNullOrEmpty(Client)
        ? _text.Get("peers", _transport == PeerTransport.Http ? "web_client_unknown" : "client_unknown")
        : Client;
    public string ConnectionHint => _text.Get("peers", _encryption switch
    {
        PeerEncryption.Plain => "plain_hint",
        PeerEncryption.Handshake => "handshake_encryption_hint",
        PeerEncryption.Rc4 => "rc4_hint",
        PeerEncryption.Tls => "tls_hint",
        _ => "encryption_unknown",
    });
    public string DownloadHint
    {
        get
        {
            if (State != PeerState.Connected)
                return StateExplanation;
            if (_transport == PeerTransport.Http)
                return _text.Format("peers", "web_download_hint", WaitText(_readWait));
            return _text.Format("peers", "transfer_hint",
                _text.Get("peers", _interested ? "interested" : "not_interested"),
                _text.Get("peers", _remoteChoked ? "remote_choked" : "remote_unchoked"),
                WaitText(_readWait));
        }
    }
    public string UploadHint
    {
        get
        {
            if (_transport == PeerTransport.Http)
                return _text.Get("peers", "web_upload_hint");
            if (State != PeerState.Connected)
                return StateExplanation;
            return _text.Format("peers", "transfer_hint",
                _text.Get("peers", _remoteInterested ? "remote_interested" : "remote_not_interested"),
                _text.Get("peers", _choked ? "choked" : "unchoked"),
                WaitText(_writeWait));
        }
    }
    public string ConnectionText
    {
        get
        {
            var transport = _transport == PeerTransport.Unknown
                ? _text.Get("peers", "unknown")
                : _transport.ToString().ToUpperInvariant();
            var direction = _text.Get("peers", Incoming ? "incoming" : "outgoing");
            var encryption = _text.Get("peers", _encryption switch
            {
                PeerEncryption.Plain or PeerEncryption.Handshake => "plain",
                PeerEncryption.Rc4 => "rc4",
                PeerEncryption.Tls => "tls",
                _ => "unknown",
            });
            return _text.Format("peers", "connection", transport, direction, encryption);
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal Peer(Strings text, string torrentId, string endpoint, Location? location, JsonElement data)
    {
        _text = text;
        TorrentId = torrentId;
        Endpoint = endpoint;
        Location = location;
        Update(data);
    }

    /// <summary>
    /// Applies the engine's latest record and raises PropertyChanged for the
    /// properties whose values changed; returns whether any did.
    /// </summary>
    internal bool Update(JsonElement data)
    {
        HashSet<string>? changed = null;
        var client = data.GetProperty("client");
        if (!client.ValueEquals(Client))
        {
            Client = client.GetString()!;
            Mark(nameof(Client), nameof(ClientText), nameof(ClientHint));
        }
        _transport = Set(_transport, data.GetProperty("transport").GetString() switch
        {
            "tcp" => PeerTransport.Tcp,
            "utp" => PeerTransport.Utp,
            "i2p" => PeerTransport.I2p,
            "http" => PeerTransport.Http,
            _ => PeerTransport.Unknown,
        }, nameof(ClientHint), nameof(DownloadHint), nameof(UploadHint), nameof(ConnectionText));
        Incoming = Set(Incoming, data.GetProperty("incoming").GetBoolean(), nameof(Incoming), nameof(ConnectionText));
        State = Set(State, data.GetProperty("state").GetString() switch
        {
            "connecting" => PeerState.Connecting,
            "handshake" => PeerState.Handshake,
            "connected" => PeerState.Connected,
            _ => PeerState.Unknown,
        }, nameof(State), nameof(StateText), nameof(StateGlyph), nameof(StateHint), nameof(DownloadHint),
            nameof(UploadHint));
        _encryption = Set(_encryption, data.GetProperty("encryption").GetString() switch
        {
            "plain" => PeerEncryption.Plain,
            "handshake" => PeerEncryption.Handshake,
            "rc4" => PeerEncryption.Rc4,
            "tls" => PeerEncryption.Tls,
            _ => PeerEncryption.Unknown,
        }, nameof(ConnectionHint), nameof(ConnectionText));
        var sources = PeerSource.None;
        foreach (var source in data.GetProperty("sources").EnumerateArray())
            sources |= source.GetString() switch
            {
                "tracker" => PeerSource.Tracker,
                "dht" => PeerSource.Dht,
                "pex" => PeerSource.Pex,
                "lsd" => PeerSource.Lsd,
                "resume" => PeerSource.Resume,
                "incoming" => PeerSource.Incoming,
                _ => PeerSource.None,
            };
        _sources = Set(_sources, sources, nameof(StateHint));
        _interested = Set(_interested, data.GetProperty("interested").GetBoolean(), nameof(DownloadHint));
        _choked = Set(_choked, data.GetProperty("choked").GetBoolean(), nameof(UploadHint));
        _remoteInterested = Set(_remoteInterested, data.GetProperty("remote_interested").GetBoolean(),
            nameof(UploadHint));
        _remoteChoked = Set(_remoteChoked, data.GetProperty("remote_choked").GetBoolean(), nameof(DownloadHint));
        _readWait = Set(_readWait, ReadWaits(data.GetProperty("read_wait")), nameof(DownloadHint));
        _writeWait = Set(_writeWait, ReadWaits(data.GetProperty("write_wait")), nameof(UploadHint));
        Progress = Set(Progress, data.GetProperty("progress").GetDouble(), nameof(Progress), nameof(ProgressText));
        DownloadRate = Set(DownloadRate, data.GetProperty("download_rate").GetDouble(),
            nameof(DownloadRate), nameof(DownloadText));
        UploadRate = Set(UploadRate, data.GetProperty("upload_rate").GetDouble(),
            nameof(UploadRate), nameof(UploadText));
        Downloaded = Set(Downloaded, data.GetProperty("downloaded").GetInt64(),
            nameof(Downloaded), nameof(DownloadedText));
        Uploaded = Set(Uploaded, data.GetProperty("uploaded").GetInt64(), nameof(Uploaded), nameof(UploadedText));
        if (changed is null)
            return false;
        foreach (var name in changed)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;

        // Each value names the properties that read it, so a row rebinds only
        // what changed.
        T Set<T>(T previous, T value, params ReadOnlySpan<string> names)
        {
            if (!EqualityComparer<T>.Default.Equals(previous, value))
                Mark(names);
            return value;
        }

        void Mark(params ReadOnlySpan<string> names)
        {
            changed ??= [];
            foreach (var name in names)
                changed.Add(name);
        }
    }

    internal void RefreshText() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    private string StateExplanation => _text.Get("peers", State switch
    {
        PeerState.Connecting => "connecting_hint",
        PeerState.Handshake => "handshake_hint",
        PeerState.Connected => "connected_hint",
        _ => "unknown",
    });

    private string SourcesText
    {
        get
        {
            List<string> sources = [];
            if (_sources.HasFlag(PeerSource.Tracker))
                sources.Add(_text.Get("peers", "source_tracker"));
            if (_sources.HasFlag(PeerSource.Dht))
                sources.Add(_text.Get("peers", "source_dht"));
            if (_sources.HasFlag(PeerSource.Pex))
                sources.Add(_text.Get("peers", "source_pex"));
            if (_sources.HasFlag(PeerSource.Lsd))
                sources.Add(_text.Get("peers", "source_lsd"));
            if (_sources.HasFlag(PeerSource.Resume))
                sources.Add(_text.Get("peers", "source_resume"));
            if (_sources.HasFlag(PeerSource.Incoming))
                sources.Add(_text.Get("peers", "source_incoming"));
            return sources.Count == 0 ? _text.Get("peers", "unknown") : string.Join(", ", sources);
        }
    }

    private static TransferWait ReadWaits(JsonElement data)
    {
        var waits = TransferWait.None;
        foreach (var wait in data.EnumerateArray())
            waits |= wait.GetString() switch
            {
                "limit" => TransferWait.Limit,
                "network" => TransferWait.Network,
                "disk" => TransferWait.Disk,
                _ => TransferWait.None,
            };
        return waits;
    }

    private string WaitText(TransferWait waits)
    {
        List<string> reasons = [];
        if (waits.HasFlag(TransferWait.Limit))
            reasons.Add(_text.Get("peers", "wait_limit"));
        if (waits.HasFlag(TransferWait.Network))
            reasons.Add(_text.Get("peers", "wait_network"));
        if (waits.HasFlag(TransferWait.Disk))
            reasons.Add(_text.Get("peers", "wait_disk"));
        return reasons.Count == 0 ? _text.Get("peers", "wait_none") : string.Join(", ", reasons);
    }

    public override string ToString() => Endpoint;
}
