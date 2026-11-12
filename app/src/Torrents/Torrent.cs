using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Engine;

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

    // Whether the person stopped the torrent: its intent, which the engine
    // calls paused. State shows Stopped only while nothing else, such as a
    // check, shows instead.
    public bool IsStopped { get; private set; }

    // The torrent's lost files that wait for the person's answer; null when
    // none wait.
    public Question? Question { get; private set; }

    // The count and size of the question's files, such as "3 files missing ·
    // 4.2 GiB"; empty without a question.
    internal string MissingText =>
        Question is { } question
            ? strings.Format(
                "units",
                "detail",
                strings.FormatCount("missing", "count", question.Files),
                strings.Bytes(question.Size)
            )
            : string.Empty;

    // The status column names a waiting job by its short label; StatusTip
    // says that it waits, and counts the files of a question.
    public string Status =>
        State switch
        {
            TorrentState.WaitingCheck => strings.Get("status", "check"),
            TorrentState.WaitingMove => strings.Get("status", "move"),
            _ => StateText,
        };
    public bool IsPaused => State is TorrentState.Paused or TorrentState.AllPaused;

    // Whether it opens or holds peer connections, so its rates show as
    // numbers: zero means connections are open but nothing moves, and a dash
    // that none are, as for a stopped, queued or failed torrent.
    public bool OpensConnections =>
        _connected
        && State
            is TorrentState.Downloading
                or TorrentState.Seeding
                or TorrentState.Completed
                or TorrentState.FilesMissing
                or TorrentState.Metadata;

    // The row names its problem in ErrorCode. The engine names one whenever it
    // classifies the torrent as Error, and also for some problems while the
    // torrent keeps another state, such as a hash failure while it downloads.
    public bool IsError => ErrorCode.Length > 0;

    // A move failed because its source folder is unavailable, so downloading
    // again at the destination recovers the torrent.
    internal bool IsSourceUnavailable => ErrorCode == "move_source_unavailable";

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
    // The waiting work, left unfinished at the last exit, waits for the
    // person to continue or abandon it.
    public bool WaitingAnswer { get; private set; }
    public string? StatusTip
    {
        get
        {
            var detail = WaitingAnswer
                ? strings.Get("status", "waiting_for_answer")
                : WaitingDrive.Length == 0
                    ? WaitingFiles.Length == 0
                        ? null
                        : strings.Format("status", "waiting_for_files", WaitingFiles)
                    : nameOf(WaitingTorrentId) is { } holder
                        ? strings.Format("status", "waiting_for_torrent", WaitingDrive, holder)
                        : strings.Format("status", "waiting_for", WaitingDrive);
            if (Question is not null)
                detail = detail is null ? MissingText : detail + " · " + MissingText;
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

    // Which torrents a filter shows, and which the Settings home counts.
    internal bool Matches(TorrentFilter filter) =>
        filter switch
        {
            TorrentFilter.All => true,
            TorrentFilter.Downloading => State is TorrentState.Downloading or TorrentState.Metadata,
            TorrentFilter.Seeding => State
                is TorrentState.Seeding
                    or TorrentState.Completed
                    or TorrentState.FilesMissing,
            TorrentFilter.Paused => State == TorrentState.Paused,
            TorrentFilter.Queued => IsWaiting,
            TorrentFilter.Errors => IsError,
            _ => false,
        };

    public string StatusGlyph => GetGlyph(State);
    public static string GetGlyph(TorrentState state) =>
        state switch
        {
            TorrentState.Downloading => Syno.Segoe.ArrowDownToLine,
            TorrentState.Seeding => Syno.Segoe.Sprout,
            TorrentState.Paused => Syno.Segoe.Square,
            TorrentState.AllPaused => Syno.Segoe.Pause,
            TorrentState.Completed => Syno.Segoe.CircleCheck,
            TorrentState.FilesMissing => Syno.Segoe.TriangleAlert,
            TorrentState.Checking => Syno.Segoe.RefreshCw,
            TorrentState.Moving => Syno.Segoe.FolderInput,
            TorrentState.Queued
            or TorrentState.WaitingCheck
            or TorrentState.WaitingMove
            or TorrentState.Metadata => Syno.Segoe.Hourglass,
            TorrentState.Error => Syno.Segoe.CircleX,
            _ => Syno.Segoe.CircleQuestionMark,
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
        [TorrentState.FilesMissing] = "files_missing",
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
            var message = ErrorCode == "move_destination_unavailable" ? strings.Get("errors", ErrorCode) : strings.Find("torrent_errors", ErrorCode)
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
        OpensConnections ? strings.Rate(DownloadRate) : Strings.NoValue;
    public string UploadText =>
        OpensConnections ? strings.Rate(UploadRate) : Strings.NoValue;
    public string DownloadName =>
        strings.Format("units", "detail", strings.Get("columns", "down"), DownloadText);
    public string UploadName =>
        strings.Format("units", "detail", strings.Get("columns", "up"), UploadText);
    public string EtaText => Eta is { } seconds ? strings.Duration(seconds) : Strings.NoValue;
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
    public string QueueText =>
        IsQueued ? (Queue + 1).ToString(CultureInfo.CurrentCulture) : Strings.NoValue;
    internal string SearchText => _searchText ??= FileName.Normalize(Name + " " + StateText);

    // The limits that are set, such as "↓ 0.5 MB/s"; empty without one.
    public string LimitText => Limits(string.Empty);

    // LimitText in words, because Narrator reads the arrows as arrows.
    public string LimitName => Limits("spoken_");

    private string Ago(DateTimeOffset? moment) => moment is { } at ? strings.Ago(at) : Strings.NoValue;

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

    /// <summary>The facts a property is computed from: each field of an engine row, the connection, and the passing of time.</summary>
    [Flags]
    private enum Fields : ulong
    {
        None = 0,
        Name = 1UL << 0,
        SavePath = 1UL << 1,
        FinalFolder = 1UL << 2,
        IsMoving = 1UL << 3,
        MoveDestination = 1UL << 4,
        Size = 1UL << 5,
        Completed = 1UL << 6,
        Downloaded = 1UL << 7,
        Uploaded = 1UL << 8,
        SeedCount = 1UL << 9,
        PeerCount = 1UL << 10,
        KnownPeerCount = 1UL << 11,
        SwarmSeedCount = 1UL << 12,
        SwarmLeecherCount = 1UL << 13,
        Queue = 1UL << 14,
        Added = 1UL << 15,
        CompletedOn = 1UL << 16,
        Hashes = 1UL << 17,
        Sequential = 1UL << 18,
        FirstLast = 1UL << 19,
        DownloadLimit = 1UL << 20,
        UploadLimit = 1UL << 21,
        Progress = 1UL << 22,
        State = 1UL << 23,
        WaitingDrive = 1UL << 24,
        WaitingTorrentId = 1UL << 25,
        WaitingFiles = 1UL << 26,
        WaitingAnswer = 1UL << 27,
        DownloadRate = 1UL << 28,
        UploadRate = 1UL << 29,
        ErrorCode = 1UL << 30,
        ErrorDetail = 1UL << 31,
        CanRetry = 1UL << 32,
        IsStopped = 1UL << 33,
        Question = 1UL << 34,
        Connected = 1UL << 35,
        // A minute passed, so the ages shown change.
        Time = 1UL << 36,
    }

    // Each property with the facts it is computed from. An update raises a
    // property only when one of them changed, so a row's bindings re-read and
    // format only what moved; one notice per property is shared by every row.
    // StatusTip and CompletedOnTip have none: their tooltips read them as they open.
    private static readonly (PropertyChangedEventArgs Notice, Fields Inputs)[] Notices =
    [
        (new(nameof(Name)), Fields.Name),
        (new(nameof(SavePath)), Fields.SavePath),
        (new(nameof(FinalFolder)), Fields.FinalFolder),
        (new(nameof(IsMoving)), Fields.IsMoving),
        (new(nameof(MoveDestination)), Fields.MoveDestination),
        (new(nameof(Size)), Fields.Size),
        (new(nameof(Completed)), Fields.Completed),
        (new(nameof(Downloaded)), Fields.Downloaded),
        (new(nameof(Uploaded)), Fields.Uploaded),
        (new(nameof(SeedCount)), Fields.SeedCount),
        (new(nameof(PeerCount)), Fields.PeerCount),
        (new(nameof(KnownPeerCount)), Fields.KnownPeerCount),
        (new(nameof(LeecherCount)), Fields.PeerCount | Fields.SeedCount),
        (new(nameof(SwarmSeedCount)), Fields.SwarmSeedCount),
        (new(nameof(SwarmLeecherCount)), Fields.SwarmLeecherCount),
        (new(nameof(Queue)), Fields.Queue),
        (new(nameof(IsQueued)), Fields.Queue),
        (new(nameof(QueueOrder)), Fields.Queue),
        (new(nameof(QueueText)), Fields.Queue),
        (new(nameof(Added)), Fields.Added),
        (new(nameof(AddedText)), Fields.Added | Fields.Time),
        (new(nameof(AddedTip)), Fields.Added),
        (new(nameof(CompletedOn)), Fields.CompletedOn),
        (new(nameof(CompletedOnText)), Fields.CompletedOn | Fields.Time),
        (new(nameof(Hashes)), Fields.Hashes),
        (new(nameof(Sequential)), Fields.Sequential),
        (new(nameof(FirstLast)), Fields.FirstLast),
        (new(nameof(DownloadLimit)), Fields.DownloadLimit),
        (new(nameof(UploadLimit)), Fields.UploadLimit),
        (new(nameof(IsLimited)), Fields.DownloadLimit | Fields.UploadLimit),
        (new(nameof(LimitText)), Fields.DownloadLimit | Fields.UploadLimit),
        (new(nameof(LimitName)), Fields.DownloadLimit | Fields.UploadLimit),
        (new(nameof(Progress)), Fields.Progress),
        (new(nameof(ProgressText)), Fields.Progress),
        (new(nameof(Remaining)), Fields.Size | Fields.Completed),
        (new(nameof(RemainingText)), Fields.Size | Fields.Completed),
        (new(nameof(Eta)), Fields.Connected | Fields.DownloadRate | Fields.Size | Fields.Completed),
        (new(nameof(EtaText)), Fields.Connected | Fields.DownloadRate | Fields.Size | Fields.Completed),
        (new(nameof(Ratio)), Fields.Downloaded | Fields.Uploaded),
        (new(nameof(RatioText)), Fields.Downloaded | Fields.Uploaded),
        (new(nameof(State)), Fields.State),
        (new(nameof(Status)), Fields.State),
        (new(nameof(StatusGlyph)), Fields.State),
        (new(nameof(IsPaused)), Fields.State),
        (new(nameof(OpensConnections)), Fields.Connected | Fields.State),
        (new(nameof(IsDownloading)), Fields.State | Fields.ErrorCode),
        (new(nameof(IsStatusNeutral)), Fields.State | Fields.ErrorCode),
        (new(nameof(IsProgressMuted)), Fields.ErrorCode | Fields.IsMoving | Fields.State | Fields.Progress),
        (new(nameof(WaitingDrive)), Fields.WaitingDrive),
        (new(nameof(WaitingTorrentId)), Fields.WaitingTorrentId),
        (new(nameof(WaitingFiles)), Fields.WaitingFiles),
        (new(nameof(WaitingAnswer)), Fields.WaitingAnswer),
        (new(nameof(DownloadRate)), Fields.DownloadRate),
        (new(nameof(UploadRate)), Fields.UploadRate),
        (new(nameof(DownloadText)), Fields.Connected | Fields.State | Fields.DownloadRate),
        (new(nameof(DownloadName)), Fields.Connected | Fields.State | Fields.DownloadRate),
        (new(nameof(UploadText)), Fields.Connected | Fields.State | Fields.UploadRate),
        (new(nameof(UploadName)), Fields.Connected | Fields.State | Fields.UploadRate),
        (new(nameof(ErrorCode)), Fields.ErrorCode),
        (new(nameof(ErrorDetail)), Fields.ErrorDetail),
        (new(nameof(IsError)), Fields.ErrorCode),
        (new(nameof(ErrorText)), Fields.ErrorCode | Fields.ErrorDetail),
        (new(nameof(CanRetry)), Fields.CanRetry),
        (new(nameof(IsStopped)), Fields.IsStopped),
        (new(nameof(Question)), Fields.Question),
        (new(nameof(SizeText)), Fields.Size),
        (new(nameof(DownloadedText)), Fields.Downloaded),
        (new(nameof(UploadedText)), Fields.Uploaded),
        (new(nameof(SeedsText)), Fields.SeedCount | Fields.SwarmSeedCount),
        (new(nameof(SeedsName)), Fields.SeedCount | Fields.SwarmSeedCount),
        (new(nameof(PeersText)), Fields.PeerCount | Fields.SeedCount | Fields.SwarmLeecherCount),
        (new(nameof(PeersName)), Fields.PeerCount | Fields.SeedCount | Fields.SwarmLeecherCount),
    ];
    private static readonly PropertyChangedEventArgs Everything = new(string.Empty);

    internal void RefreshText()
    {
        _searchText = null;
        PropertyChanged?.Invoke(this, Everything);
    }

    private void Raise(Fields changed)
    {
        if (changed == Fields.None || PropertyChanged is not { } handler)
        {
            return;
        }
        foreach (var (notice, inputs) in Notices)
        {
            if ((changed & inputs) != 0)
            {
                handler(this, notice);
            }
        }
    }

    internal void Disconnect()
    {
        _connected = false;
        Raise(Fields.Connected);
    }

    internal bool Update(JsonElement row)
    {
        var changed = _connected ? Fields.None : Fields.Connected;
        _connected = true;
        Name = ReadText("name", Name, Fields.Name);
        SavePath = ReadText("save_path", SavePath, Fields.SavePath);
        FinalFolder = ReadText("final_folder", FinalFolder, Fields.FinalFolder);
        IsMoving = Set(IsMoving, row.GetProperty("moving").GetBoolean(), Fields.IsMoving);
        MoveDestination = ReadText("move_destination", MoveDestination, Fields.MoveDestination);
        Size = Set(Size, row.GetProperty("size").GetInt64(), Fields.Size);
        Completed = Set(Completed, row.GetProperty("completed").GetInt64(), Fields.Completed);
        Downloaded = Set(Downloaded, row.GetProperty("downloaded").GetInt64(), Fields.Downloaded);
        Uploaded = Set(Uploaded, row.GetProperty("uploaded").GetInt64(), Fields.Uploaded);
        SeedCount = Set(SeedCount, row.GetProperty("seed_count").GetInt32(), Fields.SeedCount);
        PeerCount = Set(PeerCount, row.GetProperty("peer_count").GetInt32(), Fields.PeerCount);
        KnownPeerCount = Set(KnownPeerCount, row.GetProperty("known_peer_count").GetInt32(), Fields.KnownPeerCount);
        SwarmSeedCount = Set(SwarmSeedCount, row.GetProperty("swarm_seed_count").GetInt32(), Fields.SwarmSeedCount);
        SwarmLeecherCount = Set(
            SwarmLeecherCount,
            row.GetProperty("swarm_leecher_count").GetInt32(),
            Fields.SwarmLeecherCount
        );
        Queue = Set(Queue, row.GetProperty("queue").GetInt32(), Fields.Queue);
        Added = Set(Added, PipeClient.ReadTime(row.GetProperty("added")), Fields.Added);
        CompletedOn = Set(
            CompletedOn,
            PipeClient.ReadTime(row.GetProperty("completed_on")),
            Fields.CompletedOn
        );
        var hashes = row.GetProperty("hashes");
        var sameHashes = hashes.GetArrayLength() == Hashes.Length;
        for (var index = 0; sameHashes && index < Hashes.Length; index++)
            sameHashes = hashes[index].ValueEquals(Hashes[index]);
        if (!sameHashes)
        {
            Hashes = hashes.EnumerateArray().Select(hash => hash.GetString()!).ToArray();
            changed |= Fields.Hashes;
        }
        Sequential = Set(Sequential, row.GetProperty("sequential").GetBoolean(), Fields.Sequential);
        FirstLast = Set(FirstLast, row.GetProperty("first_last").GetBoolean(), Fields.FirstLast);
        DownloadLimit = Set(DownloadLimit, row.GetProperty("download_limit").GetInt32(), Fields.DownloadLimit);
        UploadLimit = Set(UploadLimit, row.GetProperty("upload_limit").GetInt32(), Fields.UploadLimit);
        Progress = Set(Progress, row.GetProperty("progress").GetDouble(), Fields.Progress);
        State = Set(State, ReadState(row.GetProperty("status")), Fields.State);
        WaitingDrive = ReadText("waiting_drive", WaitingDrive, Fields.WaitingDrive);
        WaitingTorrentId = ReadText("waiting_torrent_id", WaitingTorrentId, Fields.WaitingTorrentId);
        WaitingFiles = ReadText("waiting_files", WaitingFiles, Fields.WaitingFiles);
        WaitingAnswer = Set(WaitingAnswer, row.GetProperty("waiting_answer").GetBoolean(), Fields.WaitingAnswer);
        DownloadRate = Set(DownloadRate, row.GetProperty("download_rate").GetDouble(), Fields.DownloadRate);
        UploadRate = Set(UploadRate, row.GetProperty("upload_rate").GetDouble(), Fields.UploadRate);
        ErrorCode = ReadText("error", ErrorCode, Fields.ErrorCode);
        ErrorDetail = ReadText("detail", ErrorDetail, Fields.ErrorDetail);
        CanRetry = Set(CanRetry, row.GetProperty("retry").GetBoolean(), Fields.CanRetry);
        IsStopped = Set(IsStopped, row.GetProperty("paused").GetBoolean(), Fields.IsStopped);
        ReadQuestion();
        if ((changed & (Fields.Name | Fields.State)) != 0)
        {
            _searchText = null;
        }
        var facts = changed;
        var now = DateTimeOffset.UtcNow;
        if (now - _ageRefresh >= TimeSpan.FromMinutes(1) || now < _ageRefresh)
        {
            _ageRefresh = now;
            changed |= Fields.Time;
        }
        Raise(changed);
        return facts != Fields.None;

        T Set<T>(T previous, T value, Fields field)
        {
            if (!EqualityComparer<T>.Default.Equals(previous, value))
            {
                changed |= field;
            }
            return value;
        }

        string ReadText(string name, string previous, Fields field)
        {
            var value = row.GetProperty(name);
            if (value.ValueEquals(previous))
                return previous;
            changed |= field;
            return value.GetString()!;
        }

        // A question is made only when it changed, so an unchanged one costs
        // no object at each snapshot.
        void ReadQuestion()
        {
            var files = row.GetProperty("missing_files").GetInt32();
            if (files == 0)
            {
                Question = Set(Question, null, Fields.Question);
                return;
            }
            var number = row.GetProperty("question").GetUInt64();
            var size = row.GetProperty("missing_size").GetInt64();
            if (Question is { } known && known.Number == number && known.Files == files && known.Size == size)
                return;
            Question = new Question(number, files, size);
            changed |= Fields.Question;
        }
    }
}

// A torrent's question as the engine reports it: the count and size of its
// files, and its number, which rises at each change of the question. An
// answer sends the number back, so it acts only on the files it was shown.
public sealed record Question(ulong Number, int Files, long Size);
