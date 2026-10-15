using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Models;

public sealed class Torrent(string torrentId, Strings strings) : INotifyPropertyChanged
{
    private bool _connected;
    private DateTimeOffset _ageRefresh;
    private string? _searchText;
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
    public int KnownPeerCount { get; private set; }
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
    public bool IsProgressMuted =>
        !IsError && !IsMoving && (IsPaused || (Progress >= 1 && StatusCode != "checking"));
    public bool ShowsProgress => !IsMoving || IsError;
    public bool IsDownloading => StatusCode == "downloading" && !IsError;
    public bool IsStatusNeutral => !IsDownloading && !IsError;
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
    internal string SearchText => _searchText ??= FileName.Normalize(Name + " " + Status);

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

    internal void RefreshText()
    {
        _searchText = null;
        Notify();
    }

    private void Notify() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    internal void Disconnect()
    {
        _connected = false;
        Notify();
    }

    internal bool Update(JsonElement row)
    {
        var changed = !_connected;
        _connected = true;
        var previousName = Name;
        var previousStatus = StatusCode;
        Name = ReadText("name", Name);
        SavePath = ReadText("save_path", SavePath);
        FinalFolder = ReadText("final_folder", FinalFolder);
        IsMoving = Set(IsMoving, row.GetProperty("moving").GetBoolean());
        MoveDestination = ReadText("move_destination", MoveDestination);
        Size = Set(Size, row.GetProperty("size").GetInt64());
        Completed = Set(Completed, row.GetProperty("completed").GetInt64());
        Downloaded = Set(Downloaded, row.GetProperty("downloaded").GetInt64());
        Uploaded = Set(Uploaded, row.GetProperty("uploaded").GetInt64());
        SeedCount = Set(SeedCount, row.GetProperty("seed_count").GetInt32());
        PeerCount = Set(PeerCount, row.GetProperty("peer_count").GetInt32());
        KnownPeerCount = Set(KnownPeerCount, row.GetProperty("known_peer_count").GetInt32());
        SwarmSeedCount = Set(SwarmSeedCount, row.GetProperty("swarm_seed_count").GetInt32());
        SwarmLeecherCount = Set(
            SwarmLeecherCount,
            row.GetProperty("swarm_leecher_count").GetInt32()
        );
        Queue = Set(Queue, row.GetProperty("queue").GetInt32());
        Added = Set(Added, Moment(row.GetProperty("added")));
        CompletedOn = Set(CompletedOn, Moment(row.GetProperty("completed_on")));
        var hashes = row.GetProperty("hashes");
        var sameHashes = hashes.GetArrayLength() == Hashes.Length;
        for (var index = 0; sameHashes && index < Hashes.Length; index++)
            sameHashes = hashes[index].ValueEquals(Hashes[index]);
        if (!sameHashes)
        {
            Hashes = hashes.EnumerateArray().Select(hash => hash.GetString()!).ToArray();
            changed = true;
        }
        Sequential = Set(Sequential, row.GetProperty("sequential").GetBoolean());
        FirstLast = Set(FirstLast, row.GetProperty("first_last").GetBoolean());
        DownloadLimit = Set(DownloadLimit, row.GetProperty("download_limit").GetInt32());
        UploadLimit = Set(UploadLimit, row.GetProperty("upload_limit").GetInt32());
        Progress = Set(Progress, row.GetProperty("progress").GetDouble());
        StatusCode = ReadText("status", StatusCode);
        DownloadRate = Set(DownloadRate, row.GetProperty("download_rate").GetDouble());
        UploadRate = Set(UploadRate, row.GetProperty("upload_rate").GetDouble());
        ErrorCode = ReadText("error", ErrorCode);
        ErrorDetail = ReadText("detail", ErrorDetail);
        if (Name != previousName || StatusCode != previousStatus)
            _searchText = null;
        var now = DateTimeOffset.UtcNow;
        if (changed)
        {
            _ageRefresh = now;
            Notify();
        }
        else if (now - _ageRefresh >= TimeSpan.FromMinutes(1) || now < _ageRefresh)
        {
            _ageRefresh = now;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AddedText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompletedOnText)));
        }
        return changed;

        T Set<T>(T previous, T value)
        {
            changed |= !EqualityComparer<T>.Default.Equals(previous, value);
            return value;
        }

        string ReadText(string name, string previous)
        {
            var value = row.GetProperty(name);
            if (value.ValueEquals(previous))
                return previous;
            changed = true;
            return value.GetString()!;
        }
    }

    // The engine sends 0 for a time it does not have.
    private static DateTimeOffset? Moment(JsonElement value)
    {
        var seconds = value.GetInt64();
        return seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
    }
}
