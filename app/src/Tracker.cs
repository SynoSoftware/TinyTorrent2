using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace Syno.TinyTorrent;

public sealed class Tracker : INotifyPropertyChanged
{
    private readonly Strings _text;
    public string Url { get; }
    public int Tier { get; }
    public TrackerStatus Status { get; }
    public int Seeds { get; }
    public int Leechers { get; }
    public int Downloaded { get; }
    public long NextAnnounce { get; }
    public string Message { get; }
    public string StatusText => _text.Get("trackers", Status.ToString().ToLowerInvariant());
    public string TierText => (Tier + 1).ToString(CultureInfo.CurrentCulture);
    public string SeedsText => Seeds < 0 ? "—" : Seeds.ToString("N0", CultureInfo.CurrentCulture);
    public string LeechersText => Leechers < 0 ? "—" : Leechers.ToString("N0", CultureInfo.CurrentCulture);
    public string DownloadedText => Downloaded < 0 ? "—" : Downloaded.ToString("N0", CultureInfo.CurrentCulture);
    public string NextText => NextAnnounce <= 0 ? "—" : DateTimeOffset.FromUnixTimeSeconds(NextAnnounce).LocalDateTime.ToString("T", CultureInfo.CurrentCulture);
    public event PropertyChangedEventHandler? PropertyChanged;

    internal Tracker(Strings text, JsonElement data)
    {
        _text = text;
        Url = data.GetProperty("url").GetString()!;
        Tier = data.GetProperty("tier").GetInt32();
        Status = data.GetProperty("status").GetString() switch
        {
            "waiting" => TrackerStatus.Waiting,
            "announcing" => TrackerStatus.Announcing,
            "working" => TrackerStatus.Working,
            "error" => TrackerStatus.Error,
            "disabled" => TrackerStatus.Disabled,
            _ => TrackerStatus.Unknown
        };
        Seeds = data.GetProperty("seeds").GetInt32();
        Leechers = data.GetProperty("leechers").GetInt32();
        Downloaded = data.GetProperty("downloaded").GetInt32();
        NextAnnounce = data.GetProperty("next_announce").GetInt64();
        Message = data.GetProperty("message").GetString()!;
    }

    internal void RefreshText() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    public override string ToString() => Url;
}
