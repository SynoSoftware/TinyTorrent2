using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Syno.TinyTorrent.Shared;

namespace Syno.TinyTorrent.Torrents;

public sealed class Peer : INotifyPropertyChanged
{
    private readonly Strings _text;
    private Record _record;
    private Location? _location;

    public string Endpoint { get; }
    public string Address { get; }
    public int? Port { get; }
    public string PortText => Port?.ToString(CultureInfo.InvariantCulture) ?? Strings.NoValue;
    internal string TorrentId { get; }
    public string Client => _record.Client;
    public bool Incoming => _record.Values.Incoming;
    public PeerState State => _record.Values.State;
    internal PeerRole Role => _record.Values.Role;
    public double Progress => _record.Values.Progress;
    public double DownloadRate => _record.Values.DownloadRate;
    public double UploadRate => _record.Values.UploadRate;
    public long Downloaded => _record.Values.Downloaded;
    public long Uploaded => _record.Values.Uploaded;
    public string ProgressText =>
        State == PeerState.Known
            ? Strings.NoValue
            : Progress.ToString("P1", CultureInfo.CurrentCulture);
    public string DownloadText =>
        State == PeerState.Known ? Strings.NoValue : _text.Rate(DownloadRate);
    public string UploadText =>
        State == PeerState.Known ? Strings.NoValue : _text.Rate(UploadRate);
    public string DownloadedText =>
        State == PeerState.Known ? Strings.NoValue : _text.Bytes(Downloaded);
    public string UploadedText =>
        State == PeerState.Known ? Strings.NoValue : _text.Bytes(Uploaded);
    public string ClientText => string.IsNullOrEmpty(Client) ? Strings.NoValue : Client;

    /// <summary>The ISO country code that names the peer's flag, or empty without a location.</summary>
    public string CountryCode => Location?.Code ?? string.Empty;
    public string CountryText => Location?.CountryName(_text.Language) ?? Strings.NoValue;
    public string CityText => Location?.City?.Name(_text.Language) ?? Strings.NoValue;
    public string LocationText =>
        Location?.City?.Name(_text.Language) is { } city
            ? _text.Format("peers", "location_city", city, CountryText)
            : CountryText;

    internal Location? Location
    {
        get => Volatile.Read(ref _location);
        set
        {
            if (_location == value)
            {
                return;
            }
            Volatile.Write(ref _location, value);
            Raise(Located);
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
                _record.Values.Transport == PeerTransport.Http ? "web_client_unknown" : "client_unknown"
            )
            : Client;

    /// <summary>Why no client is shown, or null when the peer named its client.</summary>
    public string? UnknownClientHint => string.IsNullOrEmpty(Client) ? ClientHint : null;
    public string DirectionGlyph =>
        State == PeerState.Known ? string.Empty : Incoming ? Syno.Segoe.ArrowDown : Syno.Segoe.ArrowUp;
    public string EncryptionGlyph =>
        State == PeerState.Known
            ? string.Empty
            : _record.Values.Encryption switch
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
                    _record.Values.Encryption switch
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
            var values = _record.Values;
            if (State != PeerState.Connected)
                return StateExplanation;
            if (values.Transport == PeerTransport.Http)
                return _text.Format("peers", "web_download_hint", WaitText(values.ReadWait));
            return _text.Format(
                "peers",
                "transfer_hint",
                _text.Get("peers", values.Interested ? "interested" : "not_interested"),
                _text.Get("peers", values.RemoteChoked ? "remote_choked" : "remote_unchoked"),
                WaitText(values.ReadWait)
            );
        }
    }
    public string UploadHint
    {
        get
        {
            var values = _record.Values;
            if (values.Transport == PeerTransport.Http)
                return _text.Get("peers", "web_upload_hint");
            if (State != PeerState.Connected)
                return StateExplanation;
            return _text.Format(
                "peers",
                "transfer_hint",
                _text.Get(
                    "peers",
                    values.RemoteInterested ? "remote_interested" : "remote_not_interested"
                ),
                _text.Get("peers", values.Choked ? "choked" : "unchoked"),
                WaitText(values.WriteWait)
            );
        }
    }
    public string ConnectionText => State == PeerState.Known ? Strings.NoValue : TransportText;

    /// <summary>The connection's raw values, which the Connection column sorts by.</summary>
    internal (PeerTransport Transport, bool Incoming, PeerEncryption Encryption) Connection
    {
        get
        {
            var values = _record.Values;
            return (values.Transport, values.Incoming, values.Encryption);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal Peer(
        Strings text,
        string torrentId,
        string endpoint,
        Location? location,
        JsonElement data
    ) : this(text, torrentId, endpoint, location, Parse(data)) { }

    internal Peer(
        Strings text,
        string torrentId,
        string endpoint,
        Location? location,
        Record record
    ) : this(text, torrentId, endpoint, IPEndPoint.TryParse(endpoint, out var parsed) ? parsed : null, location, record) { }

    /// <param name="parsed">The endpoint parsed, or null when it is not an IP endpoint.</param>
    internal Peer(
        Strings text,
        string torrentId,
        string endpoint,
        IPEndPoint? parsed,
        Location? location,
        Record record
    )
    {
        _text = text;
        TorrentId = torrentId;
        Endpoint = endpoint;
        Address = parsed?.Address.ToString() ?? endpoint;
        Port = parsed?.Port;
        Location = location;
        _record = record;
    }


    /// <summary>What one engine reply says about a peer, apart from its endpoint and client name.</summary>
    internal struct Values
    {
        internal PeerState State;
        internal PeerRole Role;
        internal PeerTransport Transport;
        internal PeerEncryption Encryption;
        internal PeerSource Sources;
        internal bool Incoming;
        internal bool Interested;
        internal bool Choked;
        internal bool RemoteInterested;
        internal bool RemoteChoked;
        internal TransferWait ReadWait;
        internal TransferWait WriteWait;
        internal double Progress;
        internal double DownloadRate;
        internal double UploadRate;
        internal long Downloaded;
        internal long Uploaded;

        /// <summary>A connection to the peer is open or opening: any state but known.</summary>
        internal readonly bool IsConnected => State != PeerState.Known;

        /// <summary>The peer moves data over its open connection.</summary>
        internal readonly bool IsActive =>
            State == PeerState.Connected && DownloadRate + UploadRate > 0;
    }

    /// <summary>
    /// A peer's values as one reply gave them. It never changes after it is
    /// made, so a worker reads it without a lock while the UI thread swaps in
    /// the next one.
    /// </summary>
    internal sealed class Record(Values values, string client)
    {
        internal readonly Values Values = values;
        internal readonly string Client = client;
    }

    /// <summary>
    /// The bound properties a change raises, one bit each, and the
    /// <see cref="Peer.Connection"/> sort key, whose bit raises nothing
    /// because no binding reads it.
    /// </summary>
    [Flags]
    internal enum Properties
    {
        None = 0,
        Role = 1 << 0,
        Client = 1 << 1,
        ClientText = 1 << 2,
        ClientHint = 1 << 3,
        Incoming = 1 << 4,
        DirectionGlyph = 1 << 5,
        ConnectionHint = 1 << 6,
        ConnectionText = 1 << 7,
        DownloadHint = 1 << 8,
        UploadHint = 1 << 9,
        State = 1 << 10,
        StateText = 1 << 11,
        StateGlyph = 1 << 12,
        StateBadge = 1 << 13,
        StateHint = 1 << 14,
        EncryptionGlyph = 1 << 15,
        Progress = 1 << 16,
        ProgressText = 1 << 17,
        DownloadRate = 1 << 18,
        DownloadText = 1 << 19,
        UploadRate = 1 << 20,
        UploadText = 1 << 21,
        Downloaded = 1 << 22,
        DownloadedText = 1 << 23,
        Uploaded = 1 << 24,
        UploadedText = 1 << 25,
        CountryCode = 1 << 26,
        CountryText = 1 << 27,
        CityText = 1 << 28,
        LocationText = 1 << 29,
        Connection = 1 << 30,
    }

    /// <summary>The properties a change of location raises.</summary>
    internal const Properties Located =
        Properties.CountryCode | Properties.CountryText | Properties.CityText | Properties.LocationText;

    // One notice per property, shared by every peer, because bindings only
    // read the property name from it.
    private static readonly (Properties Property, PropertyChangedEventArgs Notice)[] Notices =
    [
        (Properties.Role, new(nameof(Role))),
        (Properties.Client, new(nameof(Client))),
        (Properties.ClientText, new(nameof(ClientText))),
        (Properties.ClientHint, new(nameof(ClientHint))),
        (Properties.Incoming, new(nameof(Incoming))),
        (Properties.DirectionGlyph, new(nameof(DirectionGlyph))),
        (Properties.ConnectionHint, new(nameof(ConnectionHint))),
        (Properties.ConnectionText, new(nameof(ConnectionText))),
        (Properties.DownloadHint, new(nameof(DownloadHint))),
        (Properties.UploadHint, new(nameof(UploadHint))),
        (Properties.State, new(nameof(State))),
        (Properties.StateText, new(nameof(StateText))),
        (Properties.StateGlyph, new(nameof(StateGlyph))),
        (Properties.StateBadge, new(nameof(StateBadge))),
        (Properties.StateHint, new(nameof(StateHint))),
        (Properties.EncryptionGlyph, new(nameof(EncryptionGlyph))),
        (Properties.Progress, new(nameof(Progress))),
        (Properties.ProgressText, new(nameof(ProgressText))),
        (Properties.DownloadRate, new(nameof(DownloadRate))),
        (Properties.DownloadText, new(nameof(DownloadText))),
        (Properties.UploadRate, new(nameof(UploadRate))),
        (Properties.UploadText, new(nameof(UploadText))),
        (Properties.Downloaded, new(nameof(Downloaded))),
        (Properties.DownloadedText, new(nameof(DownloadedText))),
        (Properties.Uploaded, new(nameof(Uploaded))),
        (Properties.UploadedText, new(nameof(UploadedText))),
        (Properties.CountryCode, new(nameof(CountryCode))),
        (Properties.CountryText, new(nameof(CountryText))),
        (Properties.CityText, new(nameof(CityText))),
        (Properties.LocationText, new(nameof(LocationText))),
    ];
    private static readonly PropertyChangedEventArgs Everything = new(string.Empty);

    /// <summary>A peer's record and location after a reply, and the properties they change.</summary>
    internal readonly record struct Change(Record Record, Location? Location, Properties Properties);

    // Thousands of peers repeat a few dozen client names, and a known peer has
    // only its role and sources, so peers share these. The names grow only
    // with the distinct clients seen.
    private static readonly ConcurrentDictionary<string, string> ClientNames = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> ClientLookup =
        ClientNames.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly ConcurrentDictionary<(PeerRole Role, PeerSource Sources), Record> KnownRecords = new();

    /// <summary>The record of a reply's values, from <see cref="Read"/>, shared where it can be.</summary>
    internal static Record Make(in Values values, ReadOnlySpan<char> client)
    {
        if (values.State == PeerState.Known)
        {
            return KnownRecords.GetOrAdd(
                (values.Role, values.Sources),
                static key => new(new() { State = PeerState.Known, Role = key.Role, Sources = key.Sources }, string.Empty)
            );
        }
        return new(values, ClientName(client));
    }

    private static string ClientName(ReadOnlySpan<char> client)
    {
        if (client.IsEmpty)
        {
            return string.Empty;
        }
        if (ClientLookup.TryGetValue(client, out var shared))
        {
            return shared;
        }
        var name = client.ToString();
        return ClientNames.GetOrAdd(name, name);
    }

    // An endpoint or client name longer than this, which the engine does not
    // send, is read into a new string instead of the caller's buffer.
    internal const int TextLength = 256;

    /// <summary>
    /// Reads one peer object of an engine reply, from the reply's parsed
    /// document. The endpoint and client name are copied into the buffers,
    /// each of <see cref="TextLength"/> characters, so a peer is read without
    /// an allocation. A missing endpoint is empty.
    /// </summary>
    internal static Values Read(
        JsonElement peer,
        Span<char> endpointBuffer,
        Span<char> clientBuffer,
        out ReadOnlySpan<char> endpoint,
        out ReadOnlySpan<char> client
    )
    {
        Values values = default;
        endpoint = default;
        client = default;
        Span<char> word = stackalloc char[24];
        foreach (var property in peer.EnumerateObject())
        {
            var value = property.Value;
            switch (Word(JsonMarshal.GetRawUtf8PropertyName(property), word))
            {
                case "endpoint":
                    endpoint = Copy(value, endpointBuffer);
                    break;
                case "client":
                    client = Copy(value, clientBuffer);
                    break;
                case "state":
                    values.State = Word(Raw(value), word) switch
                    {
                        "known" => PeerState.Known,
                        "connecting" => PeerState.Connecting,
                        "handshake" => PeerState.Handshake,
                        "connected" => PeerState.Connected,
                        _ => PeerState.Unknown,
                    };
                    break;
                case "role":
                    values.Role = Word(Raw(value), word) switch
                    {
                        "seed" => PeerRole.Seed,
                        "leecher" => PeerRole.Leecher,
                        _ => PeerRole.Unknown,
                    };
                    break;
                case "transport":
                    values.Transport = Word(Raw(value), word) switch
                    {
                        "tcp" => PeerTransport.Tcp,
                        "utp" => PeerTransport.Utp,
                        "i2p" => PeerTransport.I2p,
                        "http" => PeerTransport.Http,
                        _ => PeerTransport.Unknown,
                    };
                    break;
                case "encryption":
                    values.Encryption = Word(Raw(value), word) switch
                    {
                        "plain" => PeerEncryption.Plain,
                        "handshake" => PeerEncryption.Handshake,
                        "rc4" => PeerEncryption.Rc4,
                        "tls" => PeerEncryption.Tls,
                        _ => PeerEncryption.Unknown,
                    };
                    break;
                case "sources":
                    values.Sources = ReadSources(value, word);
                    break;
                case "read_wait":
                    values.ReadWait = ReadWaits(value, word);
                    break;
                case "write_wait":
                    values.WriteWait = ReadWaits(value, word);
                    break;
                case "incoming":
                    values.Incoming = value.GetBoolean();
                    break;
                case "interested":
                    values.Interested = value.GetBoolean();
                    break;
                case "choked":
                    values.Choked = value.GetBoolean();
                    break;
                case "remote_interested":
                    values.RemoteInterested = value.GetBoolean();
                    break;
                case "remote_choked":
                    values.RemoteChoked = value.GetBoolean();
                    break;
                case "progress":
                    values.Progress = value.GetDouble();
                    break;
                case "download_rate":
                    values.DownloadRate = value.GetDouble();
                    break;
                case "upload_rate":
                    values.UploadRate = value.GetDouble();
                    break;
                case "downloaded":
                    values.Downloaded = value.GetInt64();
                    break;
                case "uploaded":
                    values.Uploaded = value.GetInt64();
                    break;
            }
        }
        // A known peer has no connection, so only its role and sources apply.
        if (values.State == PeerState.Known)
        {
            values = new() { State = PeerState.Known, Role = values.Role, Sources = values.Sources };
            client = default;
        }
        return values;
    }

    /// <summary>The properties that differ between <paramref name="previous"/> and a reply's values.</summary>
    internal static Properties Compare(Record previous, in Values next, ReadOnlySpan<char> client)
    {
        var changed = Properties.None;
        ref readonly var old = ref previous.Values;
        Check(old.Role, next.Role, Properties.Role);
        if (!client.SequenceEqual(previous.Client))
        {
            changed |= Properties.Client | Properties.ClientText | Properties.ClientHint;
        }
        Check(old.Transport, next.Transport,
            Properties.ClientHint
            | Properties.DownloadHint
            | Properties.UploadHint
            | Properties.ConnectionHint
            | Properties.ConnectionText
            | Properties.Connection
        );
        Check(old.Incoming, next.Incoming,
            Properties.Incoming | Properties.DirectionGlyph | Properties.ConnectionHint | Properties.Connection
        );
        Check(old.State, next.State,
            Properties.State
            | Properties.StateText
            | Properties.StateGlyph
            | Properties.StateBadge
            | Properties.StateHint
            | Properties.ProgressText
            | Properties.DownloadText
            | Properties.UploadText
            | Properties.DownloadedText
            | Properties.UploadedText
            | Properties.DirectionGlyph
            | Properties.EncryptionGlyph
            | Properties.ConnectionHint
            | Properties.ConnectionText
            | Properties.DownloadHint
            | Properties.UploadHint
        );
        Check(old.Encryption, next.Encryption,
            Properties.EncryptionGlyph | Properties.ConnectionHint | Properties.Connection
        );
        Check(old.Sources, next.Sources, Properties.StateHint);
        Check(old.Interested, next.Interested, Properties.DownloadHint);
        Check(old.Choked, next.Choked, Properties.UploadHint);
        Check(old.RemoteInterested, next.RemoteInterested, Properties.UploadHint);
        Check(old.RemoteChoked, next.RemoteChoked, Properties.DownloadHint);
        Check(old.ReadWait, next.ReadWait, Properties.DownloadHint);
        Check(old.WriteWait, next.WriteWait, Properties.UploadHint);
        Check(old.Progress, next.Progress, Properties.Progress | Properties.ProgressText);
        Check(old.DownloadRate, next.DownloadRate, Properties.DownloadRate | Properties.DownloadText);
        Check(old.UploadRate, next.UploadRate, Properties.UploadRate | Properties.UploadText);
        Check(old.Downloaded, next.Downloaded, Properties.Downloaded | Properties.DownloadedText);
        Check(old.Uploaded, next.Uploaded, Properties.Uploaded | Properties.UploadedText);
        return changed;

        void Check<T>(T before, T value, Properties properties)
        {
            if (!EqualityComparer<T>.Default.Equals(before, value))
            {
                changed |= properties;
            }
        }
    }

    internal Record Capture() => Volatile.Read(ref _record);

    internal bool Update(Peer data)
    {
        var record = data._record;
        var changed = Compare(_record, record.Values, record.Client);
        if (changed == Properties.None)
        {
            return false;
        }
        Apply(new(record, Location, changed));
        return true;
    }

    internal void Apply(Change change)
    {
        Volatile.Write(ref _record, change.Record);
        Volatile.Write(ref _location, change.Location);
        Raise(change.Properties);
    }

    private void Raise(Properties properties)
    {
        if (PropertyChanged is not { } handler)
        {
            return;
        }
        foreach (var (property, notice) in Notices)
        {
            if ((properties & property) != 0)
            {
                handler(this, notice);
            }
        }
    }

    internal void RefreshText() => PropertyChanged?.Invoke(this, Everything);

    private string TransportText =>
        _record.Values.Transport switch
        {
            PeerTransport.Tcp => "TCP",
            PeerTransport.Utp => "UTP",
            PeerTransport.I2p => "I2P",
            PeerTransport.Http => "HTTP",
            _ => _text.Get("peers", "unknown"),
        };

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
            var known = _record.Values.Sources;
            List<string> sources = [];
            if (known.HasFlag(PeerSource.Tracker))
                sources.Add(_text.Get("peers", "source_tracker"));
            if (known.HasFlag(PeerSource.Dht))
                sources.Add(_text.Get("peers", "source_dht"));
            if (known.HasFlag(PeerSource.Pex))
                sources.Add(_text.Get("peers", "source_pex"));
            if (known.HasFlag(PeerSource.Lsd))
                sources.Add(_text.Get("peers", "source_lsd"));
            if (known.HasFlag(PeerSource.Resume))
                sources.Add(_text.Get("peers", "source_resume"));
            if (known.HasFlag(PeerSource.Incoming))
                sources.Add(_text.Get("peers", "source_incoming"));
            return sources.Count == 0 ? _text.Get("peers", "unknown") : string.Join(", ", sources);
        }
    }

    private static Record Parse(JsonElement data)
    {
        var values = Read(data, new char[TextLength], new char[TextLength], out _, out var client);
        return Make(values, client);
    }

    // The text of a short JSON token, such as a property name or an enum
    // value; anything longer than the buffer matches nothing.
    private static ReadOnlySpan<char> Word(ReadOnlySpan<byte> utf8, Span<char> buffer) =>
        utf8.Length <= buffer.Length ? buffer[..Encoding.UTF8.GetChars(utf8, buffer)] : default;

    // A string's bytes as the reply holds them, still escaped; any other
    // value has none.
    private static ReadOnlySpan<byte> Raw(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? JsonMarshal.GetRawUtf8Value(value)[1..^1] : default;

    // Text without escapes, the engine's usual text, decodes straight into the
    // buffer; text with escapes, or longer than the buffer, becomes a string.
    private static ReadOnlySpan<char> Copy(JsonElement value, Span<char> buffer)
    {
        var raw = Raw(value);
        return raw.Length <= buffer.Length && !raw.Contains((byte)'\\')
            ? buffer[..Encoding.UTF8.GetChars(raw, buffer)]
            : value.GetString();
    }

    private static PeerSource ReadSources(JsonElement value, scoped Span<char> word)
    {
        var sources = PeerSource.None;
        if (value.ValueKind != JsonValueKind.Array)
        {
            return sources;
        }
        foreach (var source in value.EnumerateArray())
        {
            sources |= Word(Raw(source), word) switch
            {
                "tracker" => PeerSource.Tracker,
                "dht" => PeerSource.Dht,
                "pex" => PeerSource.Pex,
                "lsd" => PeerSource.Lsd,
                "resume" => PeerSource.Resume,
                "incoming" => PeerSource.Incoming,
                _ => PeerSource.None,
            };
        }
        return sources;
    }

    private static TransferWait ReadWaits(JsonElement value, scoped Span<char> word)
    {
        var waits = TransferWait.None;
        if (value.ValueKind != JsonValueKind.Array)
        {
            return waits;
        }
        foreach (var wait in value.EnumerateArray())
        {
            waits |= Word(Raw(wait), word) switch
            {
                "limit" => TransferWait.Limit,
                "network" => TransferWait.Network,
                "disk" => TransferWait.Disk,
                _ => TransferWait.None,
            };
        }
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
