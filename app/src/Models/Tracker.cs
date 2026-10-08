using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Models;

public sealed class Tracker : INotifyPropertyChanged
{
    private readonly Strings _text;
    public string Url { get; }
    public int Tier { get; private set; }
    public TrackerStatus Status { get; private set; }
    public int SeedCount { get; private set; }
    public int LeecherCount { get; private set; }
    public int DownloadCount { get; private set; }
    public long NextAnnounce { get; private set; }
    public string Message { get; private set; } = string.Empty;
    public string StatusText => _text.Get("trackers", Status.ToString().ToLowerInvariant());
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
        Tier = data.GetProperty("tier").GetInt32();
        Status = data.GetProperty("status").GetString() switch
        {
            "waiting" => TrackerStatus.Waiting,
            "announcing" => TrackerStatus.Announcing,
            "working" => TrackerStatus.Working,
            "error" => TrackerStatus.Error,
            "disabled" => TrackerStatus.Disabled,
            _ => TrackerStatus.Unknown,
        };
        SeedCount = data.GetProperty("seed_count").GetInt32();
        LeecherCount = data.GetProperty("leecher_count").GetInt32();
        DownloadCount = data.GetProperty("download_count").GetInt32();
        NextAnnounce = data.GetProperty("next_announce").GetInt64();
        Message = data.GetProperty("message").GetString()!;
        RefreshText();
    }

    internal void RefreshText() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public override string ToString() => Url;
}
