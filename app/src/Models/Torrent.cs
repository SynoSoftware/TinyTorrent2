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
    public string FinalFolder { get; private set; } = string.Empty;
    public bool IsMoving { get; private set; }
    public string MoveDestination { get; private set; } = string.Empty;
    public long Size { get; private set; }
    public long Completed { get; private set; }
    public long Downloaded { get; private set; }
    public long Uploaded { get; private set; }
    public int SeedCount { get; private set; }
    public int PeerCount { get; private set; }
    public int LeecherCount => PeerCount - SeedCount;
    public int SwarmSeedCount { get; private set; }
    public int SwarmLeecherCount { get; private set; }

    // The engine uses -1 for torrents outside the download queue.
    public int Queue { get; private set; }
    public bool IsQueued => Queue >= 0;
    public int QueueOrder => IsQueued ? Queue : int.MaxValue;
    public DateTimeOffset? Added { get; private set; }
    public DateTimeOffset? CompletedOn { get; private set; }
    public string[] Hashes { get; private set; } = [];
    public bool Sequential { get; private set; }
    public bool FirstLast { get; private set; }

    // In bytes a second; 0 means no limit.
    public int DownloadLimit { get; private set; }
    public int UploadLimit { get; private set; }
    public bool IsLimited => DownloadLimit > 0 || UploadLimit > 0;
    public double Progress { get; private set; }
    public long Remaining => Size - Completed;
    public double? Eta =>
        _connected && DownloadRate > 0 && Remaining > 0 ? Remaining / DownloadRate : null;
    public double Ratio =>
        Downloaded == 0
            ? Uploaded == 0
                ? 0
                : double.PositiveInfinity
            : (double)Uploaded / Downloaded;
    public string StatusCode { get; private set; } = string.Empty;
    public string Status => strings.Status(StatusCode);
    public bool IsPaused => StatusCode is "paused" or "all_paused";
    public bool IsError => StatusCode == "error" || ErrorCode.Length > 0;
    public bool IsProgressNormal => !IsPaused && !IsError && !IsMoving;
    public string StatusGlyph =>
        StatusCode switch
        {
            "downloading" => Syno.Lucide.ArrowDownToLine,
            "seeding" => Syno.Lucide.ArrowUpFromLine,
            "paused" => Syno.Lucide.Square,
            "all_paused" => Syno.Lucide.Pause,
            "completed" => Syno.Lucide.CircleCheck,
            "checking" => Syno.Lucide.RefreshCw,
            "moving" => Syno.Lucide.Folder,
            "metadata" => Syno.Lucide.Hourglass,
            "queued" => Syno.Lucide.Clock,
            "error" => Syno.Lucide.CircleAlert,
            _ => Syno.Lucide.CircleQuestionMark,
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
    public string ProgressText =>
        IsMoving ? "—" : Progress.ToString("P1", CultureInfo.CurrentCulture);
    public string DownloadText =>
        _connected ? strings.Format("units", "rate", strings.Bytes(DownloadRate)) : "—";
    public string UploadText =>
        _connected ? strings.Format("units", "rate", strings.Bytes(UploadRate)) : "—";
    public string DownloadName =>
        strings.Format("units", "detail", strings.Get("columns", "down"), DownloadText);
    public string UploadName =>
        strings.Format("units", "detail", strings.Get("columns", "up"), UploadText);
    public string EtaText => Eta is { } seconds ? strings.Duration(seconds) : "—";
    public string RatioText =>
        double.IsPositiveInfinity(Ratio)
            ? "∞"
            : Ratio.ToString(Downloaded == 0 ? "N0" : "N2", CultureInfo.CurrentCulture);
    public string SeedsText => strings.Format("units", "swarm", SeedCount, SwarmSeedCount);
    public string PeersText => strings.Format("units", "swarm", LeecherCount, SwarmLeecherCount);
    public string SeedsName => strings.Format("inspector", "swarm", SeedCount, SwarmSeedCount);
    public string PeersName =>
        strings.Format("inspector", "swarm", LeecherCount, SwarmLeecherCount);
    public string AddedText => Ago(Added);
    public string? AddedTip => Time(Added);
    public string CompletedOnText => Ago(CompletedOn);
    public string? CompletedOnTip => Time(CompletedOn);
    public string QueueText => IsQueued ? (Queue + 1).ToString(CultureInfo.CurrentCulture) : "—";

    // The limits that are set, such as "↓ 0.5 MB/s"; empty without one.
    public string LimitText => Limits(string.Empty);

    // LimitText in words, because Narrator reads the arrows as arrows.
    public string LimitName => Limits("spoken_");

    private string Ago(DateTimeOffset? moment) => moment is { } at ? strings.Ago(at) : "—";

    private string? Time(DateTimeOffset? moment) => moment is { } at ? strings.Time(at) : null;

    private string Limits(string style) =>
        (DownloadLimit > 0, UploadLimit > 0) switch
        {
            (true, true) => strings.Format(
                "speed_limit",
                style + "both",
                strings.SpeedLimit(DownloadLimit),
                strings.SpeedLimit(UploadLimit)
            ),
            (true, false) => strings.Format(
                "speed_limit",
                style + "download",
                strings.SpeedLimit(DownloadLimit)
            ),
            (false, true) => strings.Format(
                "speed_limit",
                style + "upload",
                strings.SpeedLimit(UploadLimit)
            ),
            _ => string.Empty,
        };

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => Name;

    internal void RefreshText() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    internal void Disconnect()
    {
        _connected = false;
        RefreshText();
    }

    internal void Update(JsonElement row)
    {
        _connected = true;
        Name = row.GetProperty("name").GetString()!;
        SavePath = row.GetProperty("save_path").GetString()!;
        FinalFolder = row.GetProperty("final_folder").GetString()!;
        IsMoving = row.GetProperty("moving").GetBoolean();
        MoveDestination = row.GetProperty("move_destination").GetString()!;
        Size = row.GetProperty("size").GetInt64();
        Completed = row.GetProperty("completed").GetInt64();
        Downloaded = row.GetProperty("downloaded").GetInt64();
        Uploaded = row.GetProperty("uploaded").GetInt64();
        SeedCount = row.GetProperty("seed_count").GetInt32();
        PeerCount = row.GetProperty("peer_count").GetInt32();
        SwarmSeedCount = row.GetProperty("swarm_seed_count").GetInt32();
        SwarmLeecherCount = row.GetProperty("swarm_leecher_count").GetInt32();
        Queue = row.GetProperty("queue").GetInt32();
        Added = Moment(row.GetProperty("added"));
        CompletedOn = Moment(row.GetProperty("completed_on"));
        Hashes = row.GetProperty("hashes")
            .EnumerateArray()
            .Select(hash => hash.GetString()!)
            .ToArray();
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

    // The engine sends 0 for a time it does not have.
    private static DateTimeOffset? Moment(JsonElement value)
    {
        var seconds = value.GetInt64();
        return seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
    }
}
