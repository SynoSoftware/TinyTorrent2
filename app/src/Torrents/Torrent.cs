using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Syno.TinyTorrent.Shared;

namespace Syno.TinyTorrent.Torrents;

// `nameOf` finds another torrent's name, or null when the list lacks it.
public sealed class Torrent(string torrentId, Strings strings, Func<string, string?> nameOf)
    : INotifyPropertyChanged
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
    public TorrentState State { get; private set; }

    // The status column names a waiting job by its short label; StatusTip
    // says that it waits.
    public string Status =>
        State switch
        {
            TorrentState.WaitingCheck => strings.Get("status", "check"),
            TorrentState.WaitingMove => strings.Get("status", "move"),
            _ => StateText,
        };
    public bool IsPaused => State is TorrentState.Paused or TorrentState.AllPaused;

    // The row names its problem in ErrorCode. The engine names one whenever it
    // classifies the torrent as Error, and also for some problems while the
    // torrent keeps another state, such as a hash failure while it downloads.
    public bool IsError => ErrorCode.Length > 0;

    // The engine could not recover the torrent by itself, so the torrent, or
    // its failed move, runs again only when the person selects Retry.
    public bool CanRetry { get; private set; }
    public bool IsProgressMuted =>
        !IsError
        && !IsMoving
        && (
            IsPaused
            || (Progress >= 1 && State is not (TorrentState.Checking or TorrentState.WaitingCheck))
        );

    // The drive a waiting check or move waits for, as its own folder names it,
    // and the torrent whose job uses it; empty while the engine does not
    // know them.
    public string WaitingDrive { get; private set; } = string.Empty;
    public string WaitingTorrentId { get; private set; } = string.Empty;
    // The name of the torrent whose file work a waiting move waits for, when
    // its drives are free.
    public string WaitingFiles { get; private set; } = string.Empty;
    public string? StatusTip
    {
        get
        {
            var detail = WaitingDrive.Length == 0
                ? WaitingFiles.Length == 0
                    ? null
                    : strings.Format("status", "waiting_for_files", WaitingFiles)
                : nameOf(WaitingTorrentId) is { } holder
                    ? strings.Format("status", "waiting_for_torrent", WaitingDrive, holder)
                    : strings.Format("status", "waiting_for", WaitingDrive);
            if (!IsWaiting)
                return detail;
            var waiting = StateText;
            return detail is null ? waiting : waiting + " · " + detail;
        }
    }

    // The list shows one of three status icons, so a downloading torrent with
    // a problem shows the error icon.
    public bool IsDownloading => State == TorrentState.Downloading && !IsError;
    public bool IsStatusNeutral => !IsDownloading && !IsError;
    private bool IsWaiting =>
        State is TorrentState.Queued or TorrentState.WaitingCheck or TorrentState.WaitingMove;
    public string StatusGlyph => GetGlyph(State);
    public static string GetGlyph(TorrentState state) =>
        state switch
        {
            TorrentState.Downloading => Syno.Lucide.ArrowDownToLine,
            TorrentState.Seeding => Syno.Lucide.Sprout,
            TorrentState.Paused => Syno.Lucide.Square,
            TorrentState.AllPaused => Syno.Lucide.Pause,
            TorrentState.Completed => Syno.Lucide.CircleCheck,
            TorrentState.Checking => Syno.Lucide.RefreshCw,
            TorrentState.Moving => Syno.Lucide.Folder,
            TorrentState.Queued
            or TorrentState.WaitingCheck
            or TorrentState.WaitingMove
            or TorrentState.Metadata => Syno.Lucide.Hourglass,
            TorrentState.Error => Syno.Lucide.CircleAlert,
            _ => Syno.Lucide.CircleQuestionMark,
        };

    // The engine's word for each torrent state, which is also the state's key
    // in the status catalogue. A word this version does not know reads as
    // Unknown and shows the unknown text.
    private static readonly Dictionary<TorrentState, string> Words = new()
    {
        [TorrentState.Unknown] = "unknown",
        [TorrentState.Downloading] = "downloading",
        [TorrentState.Seeding] = "seeding",
        [TorrentState.Completed] = "completed",
        [TorrentState.Paused] = "paused",
        [TorrentState.AllPaused] = "all_paused",
        [TorrentState.Checking] = "checking",
        [TorrentState.WaitingCheck] = "waiting_check",
        [TorrentState.Moving] = "moving",
        [TorrentState.WaitingMove] = "waiting_move",
        [TorrentState.Metadata] = "metadata",
        [TorrentState.Queued] = "queued",
        [TorrentState.Error] = "error",
    };
    private static readonly Dictionary<string, TorrentState> States = Words.ToDictionary(
        pair => pair.Value,
        pair => pair.Key
    );

    internal static TorrentState ReadState(JsonElement value) =>
        value.GetString() is { } word && States.TryGetValue(word, out var state)
            ? state
            : TorrentState.Unknown;

    private string StateText => strings.Get("status", Words[State]);
    public double DownloadRate { get; private set; }
    public double UploadRate { get; private set; }
    public string ErrorCode { get; private set; } = string.Empty;
    public string ErrorDetail { get; private set; } = string.Empty;
    public string ErrorText
    {
        get
        {
            if (!IsError)
                return string.Empty;
            var detail = ErrorDetail.ReplaceLineEndings(" ").Trim();
            if (ErrorCode is "torrent_error" or "download_path_missing" or "download_path_invalid"
                or "download_path_unavailable" or "disk_full")
            {
                var reason = ErrorCode == "torrent_error"
                    ? detail
                    : strings.Get("errors", ErrorCode);
                return string.IsNullOrWhiteSpace(reason)
                    ? strings.Get("notifications", "torrent_stopped")
                    : strings.Format("errors", "torrent_stopped", reason);
            }
            var message = strings.Find("torrent_errors", ErrorCode)
                ?? strings.Get("torrent_errors", "unknown");
            return detail.Length == 0
                ? message
                : strings.Format("errors", "line", message.TrimEnd('.', '。'), detail);
        }
    }
    public string SizeText => strings.Bytes(Size);
    public string DownloadedText => strings.Bytes(Downloaded);
    public string UploadedText => strings.Bytes(Uploaded);
    public string RemainingText => strings.Bytes(Remaining);
    public string ProgressText => Progress.ToString("P1", CultureInfo.CurrentCulture);
    public string DownloadText =>
        _connected ? strings.Rate(DownloadRate) : "—";
    public string UploadText =>
        _connected ? strings.Rate(UploadRate) : "—";
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
    internal string SearchText => _searchText ??= FileName.Normalize(Name + " " + StateText);

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
        var previousState = State;
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
        State = Set(State, ReadState(row.GetProperty("status")));
        WaitingDrive = ReadText("waiting_drive", WaitingDrive);
        WaitingTorrentId = ReadText("waiting_torrent_id", WaitingTorrentId);
        WaitingFiles = ReadText("waiting_files", WaitingFiles);
        DownloadRate = Set(DownloadRate, row.GetProperty("download_rate").GetDouble());
        UploadRate = Set(UploadRate, row.GetProperty("upload_rate").GetDouble());
        ErrorCode = ReadText("error", ErrorCode);
        ErrorDetail = ReadText("detail", ErrorDetail);
        CanRetry = Set(CanRetry, row.GetProperty("retry").GetBoolean());
        if (Name != previousName || State != previousState)
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
