using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Syno.TinyTorrent.Shared;

namespace Syno.TinyTorrent.Torrents;

public sealed class Tracker : INotifyPropertyChanged
{
    private readonly Strings _text;
    private Endpoint[] _endpoints = [];
    public string Url { get; }
    public int Tier { get; private set; }
    public TrackerStatus Status { get; private set; }
    public bool IsError => Status == TrackerStatus.Error;
    public int SeedCount { get; private set; }
    public int LeecherCount { get; private set; }
    public int DownloadCount { get; private set; }
    public long NextAnnounce { get; private set; }
    public string Message { get; private set; } = string.Empty;
    public string StatusText => _text.Get("trackers", Status.ToString().ToLowerInvariant());
    public string StatusGlyph =>
        Status switch
        {
            TrackerStatus.Waiting => Syno.Lucide.Clock,
            TrackerStatus.Announcing => Syno.Lucide.RadioTower,
            TrackerStatus.Working => Syno.Lucide.CircleCheck,
            TrackerStatus.Error => Syno.Lucide.CircleAlert,
            TrackerStatus.Disabled => Syno.Lucide.CircleOff,
            _ => Syno.Lucide.CircleQuestionMark,
        };
    public string StatusHint =>
        _endpoints.Length == 0
            ? _text.Get("trackers", "endpoints_pending")
            : string.Join("\n\n", _endpoints.Select(Describe));
    public string DownloadsHint => _text.Get("trackers", "downloads_hint");
    public string NextHint => _text.Get("trackers", "next_hint");
    public string TierText => (Tier + 1).ToString(CultureInfo.CurrentCulture);
    public string SeedsText =>
        SeedCount < 0 ? "—" : SeedCount.ToString("N0", CultureInfo.CurrentCulture);
    public string LeechersText =>
        LeecherCount < 0 ? "—" : LeecherCount.ToString("N0", CultureInfo.CurrentCulture);
    public string DownloadsText =>
        DownloadCount < 0 ? "—" : DownloadCount.ToString("N0", CultureInfo.CurrentCulture);
    public string NextText =>
        NextAnnounce <= 0
            ? "—"
            : DateTimeOffset
                .FromUnixTimeSeconds(NextAnnounce)
                .LocalDateTime.ToString("T", CultureInfo.CurrentCulture);
    public event PropertyChangedEventHandler? PropertyChanged;

    internal Tracker(Strings text, JsonElement data)
    {
        _text = text;
        Url = data.GetProperty("url").GetString()!;
        Update(data);
    }

    internal void Update(JsonElement data)
    {
        var changed = false;
        Tier = Set(Tier, data.GetProperty("tier").GetInt32());
        Status = Set(Status, ReadStatus(data.GetProperty("status")));
        SeedCount = Set(SeedCount, data.GetProperty("seed_count").GetInt32());
        LeecherCount = Set(LeecherCount, data.GetProperty("leecher_count").GetInt32());
        DownloadCount = Set(DownloadCount, data.GetProperty("download_count").GetInt32());
        NextAnnounce = Set(NextAnnounce, data.GetProperty("next_announce").GetInt64());
        var message = data.GetProperty("message");
        if (!message.ValueEquals(Message))
        {
            Message = message.GetString()!;
            changed = true;
        }
        var endpoints = data.GetProperty("endpoints");
        var sameEndpoints = endpoints.GetArrayLength() == _endpoints.Length;
        for (var index = 0; sameEndpoints && index < _endpoints.Length; index++)
            sameEndpoints = _endpoints[index].Matches(endpoints[index]);
        if (!sameEndpoints)
        {
            _endpoints = endpoints
                .EnumerateArray()
                .Select(endpoint => new Endpoint(endpoint))
                .ToArray();
            changed = true;
        }
        if (changed)
            RefreshText();

        T Set<T>(T previous, T value)
        {
            changed |= !EqualityComparer<T>.Default.Equals(previous, value);
            return value;
        }
    }

    internal void RefreshText() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public override string ToString() => Url;

    private string Describe(Endpoint endpoint)
    {
        var summary = _text.Format(
            "trackers",
            "endpoint_hint",
            endpoint.Address,
            endpoint.Version.ToUpperInvariant(),
            _text.Get("trackers", endpoint.Status.ToString().ToLowerInvariant()),
            endpoint.Failures
        );
        return endpoint.Message.Length == 0 ? summary : summary + "\n" + endpoint.Message;
    }

    private static TrackerStatus ReadStatus(JsonElement data) =>
        data.GetString() switch
        {
            "waiting" => TrackerStatus.Waiting,
            "announcing" => TrackerStatus.Announcing,
            "working" => TrackerStatus.Working,
            "error" => TrackerStatus.Error,
            "disabled" => TrackerStatus.Disabled,
            _ => TrackerStatus.Unknown,
        };

    private sealed class Endpoint(JsonElement data)
    {
        public string Address { get; } = data.GetProperty("address").GetString()!;
        public string Version { get; } = data.GetProperty("version").GetString()!;
        public TrackerStatus Status { get; } = ReadStatus(data.GetProperty("status"));
        public int Failures { get; } = data.GetProperty("failures").GetInt32();
        public string Message { get; } = data.GetProperty("message").GetString()!;

        internal bool Matches(JsonElement value) =>
            value.GetProperty("address").ValueEquals(Address)
            && value.GetProperty("version").ValueEquals(Version)
            && ReadStatus(value.GetProperty("status")) == Status
            && value.GetProperty("failures").GetInt32() == Failures
            && value.GetProperty("message").ValueEquals(Message);
    }
}
