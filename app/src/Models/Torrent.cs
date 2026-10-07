using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Models;

public sealed class Torrent(string torrentId, Strings strings) : INotifyPropertyChanged
{
    private bool _connected;
    public string TorrentId { get; } = torrentId;
    public string Name { get; private set; } = string.Empty;
    public string SavePath { get; private set; } = string.Empty;
    public bool IsMoving { get; private set; }
    public string MoveDestination { get; private set; } = string.Empty;
    public long Size { get; private set; }
    public long Completed { get; private set; }
    public long Downloaded { get; private set; }
    public long Uploaded { get; private set; }
    public int Seeds { get; private set; }
    public int Peers { get; private set; }
    public int Leechers => Peers - Seeds;
    public int SwarmSeeds { get; private set; }
    public int SwarmPeers { get; private set; }
    public int Queue { get; private set; }
    public int QueueOrder => Queue < 0 ? int.MaxValue : Queue;
    public long Added { get; private set; }
    public string[] Hashes { get; private set; } = [];
    public bool Sequential { get; private set; }
    public bool FirstLast { get; private set; }
    // In bytes a second; 0 means no limit.
    public int DownloadLimit { get; private set; }
    public int UploadLimit { get; private set; }
    public bool IsLimited => DownloadLimit > 0 || UploadLimit > 0;
    public double Progress { get; private set; }
    public long Remaining => Size - Completed;
    public double? Eta => _connected && DownloadRate > 0 && Remaining > 0 ? Remaining / DownloadRate : null;
    public double Ratio => Downloaded == 0 ? Uploaded == 0 ? 0 : double.PositiveInfinity : (double)Uploaded / Downloaded;
    public string StatusCode { get; private set; } = string.Empty;
    public string Status => strings.Status(StatusCode);
    public bool IsPaused => StatusCode is "paused" or "all_paused";
    public bool IsError => StatusCode == "error" || ErrorCode.Length > 0;
    public bool IsProgressNormal => !IsPaused && !IsError && !IsMoving;
    public string StatusGlyph => StatusCode switch
    {
        "downloading" => Syno.Lucide.ArrowDownToLine,
        "seeding" => Syno.Lucide.ArrowUpFromLine,
        "paused" or "all_paused" => Syno.Lucide.Pause,
        "completed" => Syno.Lucide.CircleCheck,
        "checking" => Syno.Lucide.RefreshCw,
        "moving" => Syno.Lucide.Folder,
        "metadata" => Syno.Lucide.Hourglass,
        "queued" => Syno.Lucide.Clock,
        "error" => Syno.Lucide.CircleAlert,
        _ => Syno.Lucide.CircleQuestionMark
    };
    public double DownloadRate { get; private set; }
    public double UploadRate { get; private set; }
    public string ErrorCode { get; private set; } = string.Empty;
    public string ErrorDetail { get; private set; } = string.Empty;
    public string ErrorText => IsError ? strings.Error(ErrorCode, ErrorDetail) : string.Empty;
    public string SizeText => strings.Bytes(Size);
    public string DownloadedText => strings.Bytes(Downloaded);
    public string UploadedText => strings.Bytes(Uploaded);
    public string RemainingText => strings.Bytes(Remaining);
    public string ProgressText => IsMoving ? "—" : Progress.ToString("P1", CultureInfo.CurrentCulture);
    public string DownloadText => _connected ? strings.Format("units", "rate", strings.Bytes(DownloadRate)) : "—";
    public string UploadText => _connected ? strings.Format("units", "rate", strings.Bytes(UploadRate)) : "—";
    public string DownloadName => strings.Format("units", "detail", strings.Get("columns", "down"), DownloadText);
    public string UploadName => strings.Format("units", "detail", strings.Get("columns", "up"), UploadText);
    public string EtaText => Eta is { } seconds ? strings.Duration(seconds) : "—";
    public string RatioText => double.IsPositiveInfinity(Ratio) ? "∞" :
        Ratio.ToString(Downloaded == 0 ? "N0" : "N2", CultureInfo.CurrentCulture);
    public string SeedsText => strings.Format("units", "swarm", Seeds, SwarmSeeds);
    public string PeersText => strings.Format("units", "swarm", Leechers, SwarmPeers);
    public string SeedsName => strings.Format("inspector", "swarm", Seeds, SwarmSeeds);
    public string PeersName => strings.Format("inspector", "swarm", Leechers, SwarmPeers);
    public string AddedText => Added <= 0 ? "—" : strings.Ago(DateTimeOffset.FromUnixTimeSeconds(Added));
    public string? AddedTip => Added <= 0 ? null : strings.Time(DateTimeOffset.FromUnixTimeSeconds(Added));
    public string QueueText => Queue < 0 ? "—" : (Queue + 1).ToString(CultureInfo.CurrentCulture);
    // The limits that are set, such as "↓ 500 KiB/s"; empty without one.
    public string LimitText => Limits(string.Empty);
    // LimitText in words, because Narrator reads the arrows as arrows.
    public string LimitName => Limits("spoken_");

    private string Limit(int limit) => limit > 0 ? strings.Format("units", "rate", strings.Bytes(limit)) : strings.Get("transfer_limits", "unlimited");

    private string Limits(string style) => (DownloadLimit > 0, UploadLimit > 0) switch
    {
        (true, true) => strings.Format("speed_limit", style + "both", Limit(DownloadLimit), Limit(UploadLimit)),
        (true, false) => strings.Format("speed_limit", style + "download", Limit(DownloadLimit)),
        (false, true) => strings.Format("speed_limit", style + "upload", Limit(UploadLimit)),
        _ => string.Empty
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => Name;

    internal void RefreshText() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    internal void Disconnect() { _connected = false; RefreshText(); }

    internal void Update(JsonElement row)
    {
        _connected = true;
        Name = row.GetProperty("name").GetString()!;
        SavePath = row.GetProperty("save_path").GetString()!;
        IsMoving = row.GetProperty("moving").GetBoolean();
        MoveDestination = row.GetProperty("move_destination").GetString()!;
        Size = row.GetProperty("size").GetInt64();
        Completed = row.GetProperty("completed").GetInt64();
        Downloaded = row.GetProperty("downloaded").GetInt64();
        Uploaded = row.GetProperty("uploaded").GetInt64();
        Seeds = row.GetProperty("seeds").GetInt32();
        Peers = row.GetProperty("peers").GetInt32();
        SwarmSeeds = row.GetProperty("swarm_seeds").GetInt32();
        SwarmPeers = row.GetProperty("swarm_peers").GetInt32();
        Queue = row.GetProperty("queue").GetInt32();
        Added = row.GetProperty("added").GetInt64();
        Hashes = row.GetProperty("hashes").EnumerateArray().Select(hash => hash.GetString()!).ToArray();
        Sequential = row.GetProperty("sequential").GetBoolean();
        FirstLast = row.GetProperty("first_last").GetBoolean();
        DownloadLimit = row.GetProperty("download_limit").GetInt32();
        UploadLimit = row.GetProperty("upload_limit").GetInt32();
        Progress = row.GetProperty("progress").GetDouble();
        StatusCode = row.GetProperty("status").GetString()!;
        DownloadRate = row.GetProperty("download_rate").GetDouble();
        UploadRate = row.GetProperty("upload_rate").GetDouble();
        ErrorCode = row.GetProperty("error").GetString()!;
        ErrorDetail = row.GetProperty("detail").GetString()!;
        RefreshText();
    }
}
