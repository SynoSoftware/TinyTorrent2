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
            TrackerStatus.Waiting => Syno.Lucide.Hourglass,
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
        Tier = data.GetProperty("tier").GetInt32();
        Status = ReadStatus(data.GetProperty("status"));
        SeedCount = data.GetProperty("seed_count").GetInt32();
        LeecherCount = data.GetProperty("leecher_count").GetInt32();
        DownloadCount = data.GetProperty("download_count").GetInt32();
        NextAnnounce = data.GetProperty("next_announce").GetInt64();
        Message = data.GetProperty("message").GetString()!;
        _endpoints = data.GetProperty("endpoints").EnumerateArray()
            .Select(endpoint => new Endpoint(endpoint)).ToArray();
    }

    internal void Update(Tracker data)
    {
        var changed = false;
        Tier = Set(Tier, data.Tier);
        Status = Set(Status, data.Status);
        SeedCount = Set(SeedCount, data.SeedCount);
        LeecherCount = Set(LeecherCount, data.LeecherCount);
        DownloadCount = Set(DownloadCount, data.DownloadCount);
        NextAnnounce = Set(NextAnnounce, data.NextAnnounce);
        Message = Set(Message, data.Message);
        if (!_endpoints.SequenceEqual(data._endpoints))
        {
            _endpoints = data._endpoints;
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

    private sealed record Endpoint
    {
        public string Address { get; }
        public string Version { get; }
        public TrackerStatus Status { get; }
        public int Failures { get; }
        public string Message { get; }

        internal Endpoint(JsonElement data)
        {
            Address = data.GetProperty("address").GetString()!;
            Version = data.GetProperty("version").GetString()!;
            Status = ReadStatus(data.GetProperty("status"));
            Failures = data.GetProperty("failures").GetInt32();
            Message = data.GetProperty("message").GetString()!;
        }
    }
}
