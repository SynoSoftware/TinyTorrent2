using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Syno.TinyTorrent.Shared;

namespace Syno.TinyTorrent.Torrents;

public sealed class Peer : INotifyPropertyChanged
{
    private readonly Strings _text;
    private Record _record;

    public string Endpoint { get; }
    public string Address { get; }
    public int? Port { get; }
    public string PortText { get; }
    internal string TorrentId { get; }
    internal string Key { get; }
    public string Client => _record.Client;
    public bool Incoming => _record.Incoming;
    public PeerState State => _record.State;
    internal PeerRole Role => _record.Role;
    public double Progress => _record.Progress;
    public double DownloadRate => _record.DownloadRate;
    public double UploadRate => _record.UploadRate;
    public long Downloaded => _record.Downloaded;
    public long Uploaded => _record.Uploaded;
    public string ProgressText =>
        State == PeerState.Known ? "—" : Progress.ToString("P1", CultureInfo.CurrentCulture);
    public string DownloadText =>
        State == PeerState.Known ? "—" : _text.Rate(DownloadRate);
    public string UploadText =>
        State == PeerState.Known ? "—" : _text.Rate(UploadRate);
    public string DownloadedText => State == PeerState.Known ? "—" : _text.Bytes(Downloaded);
    public string UploadedText => State == PeerState.Known ? "—" : _text.Bytes(Uploaded);
    public string ClientText => string.IsNullOrEmpty(Client) ? "—" : Client;

    /// <summary>The ISO country code that names the peer's flag, or empty without a location.</summary>
    public string CountryCode => Location?.Code ?? string.Empty;
    public string CountryText => Location?.CountryName(_text.Language) ?? "—";
    public string CityText => Location?.City?.Name(_text.Language) ?? "—";
    public string LocationText =>
        Location?.City?.Name(_text.Language) is { } city
            ? _text.Format("peers", "location_city", city, CountryText)
            : CountryText;

    internal Location? Location
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountryCode)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountryText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CityText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocationText)));
        }
    }

    public string StateText => _text.Get("peers", State.ToString().ToLowerInvariant());
    public string StateGlyph =>
        State switch
        {
            PeerState.Known => Syno.Segoe.Unplug,
            PeerState.Connecting => Syno.Segoe.PlugZap,
            PeerState.Handshake => Syno.Segoe.Handshake,
            PeerState.Connected => Syno.Segoe.Connected,
            _ => Syno.Segoe.CircleQuestionMark,
        };
    public string StateBadge =>
        State == PeerState.Connected ? Syno.Segoe.ConnectedCheck : string.Empty;
    public string StateHint => _text.Format("peers", "state_hint", StateExplanation, SourcesText);
    public string ClientHint =>
        string.IsNullOrEmpty(Client)
            ? _text.Get(
                "peers",
                _record.Transport == PeerTransport.Http ? "web_client_unknown" : "client_unknown"
            )
            : Client;
    public string DirectionGlyph =>
        State == PeerState.Known ? string.Empty : Incoming ? Syno.Segoe.ArrowDown : Syno.Segoe.ArrowUp;
    public string EncryptionGlyph =>
        State == PeerState.Known
            ? string.Empty
            : _record.Encryption switch
            {
                PeerEncryption.Plain or PeerEncryption.Handshake => Syno.Segoe.ShieldOff,
                PeerEncryption.Rc4 => Syno.Segoe.ShieldCheck,
                PeerEncryption.Tls => Syno.Segoe.ShieldPlus,
                _ => string.Empty,
            };
    public string ConnectionHint =>
        State == PeerState.Known
            ? StateExplanation
            : _text.Format(
                "peers",
                "connection",
                TransportText,
                _text.Get("peers", Incoming ? "incoming" : "outgoing"),
                _text.Get(
                    "peers",
                    _record.Encryption switch
                    {
                        PeerEncryption.Plain => "plain_hint",
                        PeerEncryption.Handshake => "handshake_encryption_hint",
                        PeerEncryption.Rc4 => "rc4_hint",
                        PeerEncryption.Tls => "tls_hint",
                        _ => "encryption_unknown",
                    }
                )
            );
    public string DownloadHint
    {
        get
        {
            if (State != PeerState.Connected)
                return StateExplanation;
            if (_record.Transport == PeerTransport.Http)
                return _text.Format("peers", "web_download_hint", WaitText(_record.ReadWait));
            return _text.Format(
                "peers",
                "transfer_hint",
                _text.Get("peers", _record.Interested ? "interested" : "not_interested"),
                _text.Get("peers", _record.RemoteChoked ? "remote_choked" : "remote_unchoked"),
                WaitText(_record.ReadWait)
            );
        }
    }
    public string UploadHint
    {
        get
        {
            if (_record.Transport == PeerTransport.Http)
                return _text.Get("peers", "web_upload_hint");
            if (State != PeerState.Connected)
                return StateExplanation;
            return _text.Format(
                "peers",
                "transfer_hint",
                _text.Get(
                    "peers",
                    _record.RemoteInterested ? "remote_interested" : "remote_not_interested"
                ),
                _text.Get("peers", _record.Choked ? "choked" : "unchoked"),
                WaitText(_record.WriteWait)
            );
        }
    }
    public string ConnectionText => State == PeerState.Known ? "—" : TransportText;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal Peer(
        Strings text,
        string torrentId,
        string endpoint,
        Location? location,
        JsonElement data
    ) : this(text, torrentId, endpoint, location, Record.Parse(data)) { }

    internal Peer(
        Strings text,
        string torrentId,
        string endpoint,
        Location? location,
        Record record
    )
    {
        _text = text;
        TorrentId = torrentId;
        Endpoint = endpoint;
        Key = torrentId + "/" + endpoint;
        var parsed = IPEndPoint.TryParse(endpoint, out var address) ? address : null;
        Address = parsed?.Address.ToString() ?? endpoint;
        Port = parsed?.Port;
        PortText = Port?.ToString(CultureInfo.InvariantCulture) ?? "—";
        Location = location;
        _record = record;
    }


    internal sealed record Record
    {
        internal static Record Parse(JsonElement data)
        {
            var known = data.GetProperty("state").ValueEquals("known");
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
            return new()
            {
                State = data.GetProperty("state").GetString() switch
                {
                    "known" => PeerState.Known,
                    "connecting" => PeerState.Connecting,
                    "handshake" => PeerState.Handshake,
                    "connected" => PeerState.Connected,
                    _ => PeerState.Unknown,
                },
                Role = data.TryGetProperty("role", out var role) ? role.GetString() switch
                {
                    "seed" => PeerRole.Seed,
                    "leecher" => PeerRole.Leecher,
                    _ => PeerRole.Unknown,
                } : PeerRole.Unknown,
                Client = known ? string.Empty : data.GetProperty("client").GetString()!,
                Transport = (known ? null : data.GetProperty("transport").GetString()) switch
                {
                    "tcp" => PeerTransport.Tcp,
                    "utp" => PeerTransport.Utp,
                    "i2p" => PeerTransport.I2p,
                    "http" => PeerTransport.Http,
                    _ => PeerTransport.Unknown,
                },
                Encryption = (known ? null : data.GetProperty("encryption").GetString()) switch
                {
                    "plain" => PeerEncryption.Plain,
                    "handshake" => PeerEncryption.Handshake,
                    "rc4" => PeerEncryption.Rc4,
                    "tls" => PeerEncryption.Tls,
                    _ => PeerEncryption.Unknown,
                },
                Sources = sources,
                Incoming = !known && data.GetProperty("incoming").GetBoolean(),
                Interested = !known && data.GetProperty("interested").GetBoolean(),
                Choked = !known && data.GetProperty("choked").GetBoolean(),
                RemoteInterested = !known && data.GetProperty("remote_interested").GetBoolean(),
                RemoteChoked = !known && data.GetProperty("remote_choked").GetBoolean(),
                ReadWait = known ? TransferWait.None : ReadWaits(data.GetProperty("read_wait")),
                WriteWait = known ? TransferWait.None : ReadWaits(data.GetProperty("write_wait")),
                Progress = known ? 0 : data.GetProperty("progress").GetDouble(),
                DownloadRate = known ? 0 : data.GetProperty("download_rate").GetDouble(),
                UploadRate = known ? 0 : data.GetProperty("upload_rate").GetDouble(),
                Downloaded = known ? 0 : data.GetProperty("downloaded").GetInt64(),
                Uploaded = known ? 0 : data.GetProperty("uploaded").GetInt64(),
            };
        }

        internal string Client { get; init; } = string.Empty;
        internal PeerTransport Transport { get; init; }
        internal PeerEncryption Encryption { get; init; }
        internal PeerSource Sources { get; init; }
        internal bool Incoming { get; init; }
        internal bool Interested { get; init; }
        internal bool Choked { get; init; }
        internal bool RemoteInterested { get; init; }
        internal bool RemoteChoked { get; init; }
        internal TransferWait ReadWait { get; init; }
        internal TransferWait WriteWait { get; init; }
        internal PeerState State { get; init; }
        internal PeerRole Role { get; init; }
        internal double Progress { get; init; }
        internal double DownloadRate { get; init; }
        internal double UploadRate { get; init; }
        internal long Downloaded { get; init; }
        internal long Uploaded { get; init; }
    }

    internal sealed record Change(Record Record, IReadOnlyList<string> Properties);

    internal Record Capture() => _record;

    internal bool Update(Peer data) => Apply(Prepare(_record, data._record));

    internal bool Apply(Change? change)
    {
        if (change is null)
            return false;
        _record = change.Record;
        if (PropertyChanged is not null)
            foreach (var name in change.Properties)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    internal static Change? Prepare(Record previous, Record next)
    {
        HashSet<string>? changed = null;
        Check(previous.Role, next.Role, nameof(Role));
        Check(previous.Client, next.Client, nameof(Client), nameof(ClientText), nameof(ClientHint));
        Check(previous.Transport, next.Transport,
            nameof(ClientHint),
            nameof(DownloadHint),
            nameof(UploadHint),
            nameof(ConnectionHint),
            nameof(ConnectionText)
        );
        Check(previous.Incoming, next.Incoming,
            nameof(Incoming),
            nameof(DirectionGlyph),
            nameof(ConnectionHint)
        );
        Check(previous.State, next.State,
            nameof(State),
            nameof(StateText),
            nameof(StateGlyph),
            nameof(StateBadge),
            nameof(StateHint),
            nameof(ProgressText),
            nameof(DownloadText),
            nameof(UploadText),
            nameof(DownloadedText),
            nameof(UploadedText),
            nameof(DirectionGlyph),
            nameof(EncryptionGlyph),
            nameof(ConnectionHint),
            nameof(ConnectionText),
            nameof(DownloadHint),
            nameof(UploadHint)
        );
        Check(previous.Encryption, next.Encryption,
            nameof(EncryptionGlyph),
            nameof(ConnectionHint)
        );
        Check(previous.Sources, next.Sources, nameof(StateHint));
        Check(previous.Interested, next.Interested, nameof(DownloadHint));
        Check(previous.Choked, next.Choked, nameof(UploadHint));
        Check(previous.RemoteInterested, next.RemoteInterested, nameof(UploadHint));
        Check(previous.RemoteChoked, next.RemoteChoked, nameof(DownloadHint));
        Check(previous.ReadWait, next.ReadWait, nameof(DownloadHint));
        Check(previous.WriteWait, next.WriteWait, nameof(UploadHint));
        Check(previous.Progress, next.Progress, nameof(Progress), nameof(ProgressText));
        Check(previous.DownloadRate, next.DownloadRate,
            nameof(DownloadRate),
            nameof(DownloadText)
        );
        Check(previous.UploadRate, next.UploadRate,
            nameof(UploadRate),
            nameof(UploadText)
        );
        Check(previous.Downloaded, next.Downloaded,
            nameof(Downloaded),
            nameof(DownloadedText)
        );
        Check(previous.Uploaded, next.Uploaded,
            nameof(Uploaded),
            nameof(UploadedText)
        );
        return changed is null ? null : new(next, changed.ToArray());

        void Check<T>(T before, T value, params ReadOnlySpan<string> names)
        {
            if (!EqualityComparer<T>.Default.Equals(before, value))
                Mark(names);
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

    private string TransportText =>
        _record.Transport == PeerTransport.Unknown
            ? _text.Get("peers", "unknown")
            : _record.Transport.ToString().ToUpperInvariant();

    private string StateExplanation =>
        _text.Get(
            "peers",
            State switch
            {
                PeerState.Known => "known_hint",
                PeerState.Connecting => "connecting_hint",
                PeerState.Handshake => "handshake_hint",
                PeerState.Connected => "connected_hint",
                _ => "unknown",
            }
        );

    private string SourcesText
    {
        get
        {
            List<string> sources = [];
            if (_record.Sources.HasFlag(PeerSource.Tracker))
                sources.Add(_text.Get("peers", "source_tracker"));
            if (_record.Sources.HasFlag(PeerSource.Dht))
                sources.Add(_text.Get("peers", "source_dht"));
            if (_record.Sources.HasFlag(PeerSource.Pex))
                sources.Add(_text.Get("peers", "source_pex"));
            if (_record.Sources.HasFlag(PeerSource.Lsd))
                sources.Add(_text.Get("peers", "source_lsd"));
            if (_record.Sources.HasFlag(PeerSource.Resume))
                sources.Add(_text.Get("peers", "source_resume"));
            if (_record.Sources.HasFlag(PeerSource.Incoming))
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
