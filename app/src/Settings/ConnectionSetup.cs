using System.ComponentModel;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Text.Json;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Engine;
using Syno.TinyTorrent.Shell;
using Syno.TinyTorrent.Torrents;

namespace Syno.TinyTorrent.Settings;

public sealed class ConnectionSetup : INotifyPropertyChanged
{
    private const double MinimumCapacity = 0.01;
    private const double MaximumCapacity = int.MaxValue / 125000.0;
    private readonly Editor _settings;
    private readonly MainViewModel _main;
    private readonly PipeClient _client;
    private ConnectionPhase _phase;
    private string _measuredId = string.Empty;
    private string? _scaleTestId;
    private string _testFailure = string.Empty;
    private int _remaining;
    private double _downloadBytes;
    private double _uploadBytes;
    private double _downloadSeconds;
    private double _uploadSeconds;
    private double _timeLimit;
    private bool _complete;
    private bool _longerTest;
    private ConnectionLink? _link;
    private int _linkRevision;
    private string _sessionId = string.Empty;
    private long _sequence = -1;

    internal ConnectionSetup(Editor settings, MainViewModel main, PipeClient client)
    {
        _settings = settings;
        _main = main;
        _client = client;
        _settings.PropertyChanged += (_, _) => Refresh();
    }

    // A slider's share of the connection capacity, in percent. Its top
    // position removes the pair's limits.
    private const int Unlimited = 100;
    private const int AlternativeDefault = 25;
    private static readonly TransferPreset[] Presets =
    [
        TransferPreset.Reduced,
        TransferPreset.Balanced,
        TransferPreset.FullSpeed,
    ];

    private string _download = string.Empty;
    private string _upload = string.Empty;
    private int _standardShare = Share(TransferPreset.Balanced);
    private int _alternativeShare = AlternativeDefault;
    private bool _standardMoved;
    private bool _alternativeMoved;
    private Exception? _failure;
    public Strings Text => _settings.Text;
    public bool IsPending { get; private set; }
    public bool CanLeave => !IsPending;
    public bool CanEdit =>
        _settings.CanEdit && !IsPending && !IsTesting && _phase != ConnectionPhase.Restoring;
    public bool CanApply => CanEdit && !_settings.IsPending && Changes() is { Count: > 0 };
    public bool IsTesting =>
        _phase
            is ConnectionPhase.Stopping
                or ConnectionPhase.Downloading
                or ConnectionPhase.Uploading;
    private bool CanRelease =>
        _main.IsConnected && (IsTesting || _phase == ConnectionPhase.Holding);
    private bool HasCustomRoute =>
        _settings.Proxy.IsInUse || _settings.Adapter.ConfirmedText is { Length: > 0 };
    public bool CanTest => CanEdit && !_settings.IsPending && !HasCustomRoute;
    public bool CanCancelTest => _main.IsConnected && IsTesting && !IsPending;
    public bool IsHolding => _phase == ConnectionPhase.Holding;
    /// <summary>Both directions finished, so the rates are the test's result.</summary>
    public bool HasResult => _complete;
    public string TestId { get; private set; } = string.Empty;
    public double DownloadRate { get; private set; }
    public double UploadRate { get; private set; }

    /// <summary>The top of both speed meters, in megabits a second, or null when nothing is measured or entered.</summary>
    public double? MeterScale { get; private set; }
    public double DownloadCapacity => Parse(Download);
    public double UploadCapacity => Parse(Upload);
    public bool QuickTest => !LongerTest;
    public bool LongerTest
    {
        get => _longerTest;
        set
        {
            if (_longerTest == value || !CanTest)
                return;
            _longerTest = value;
            Refresh();
        }
    }
    public string TestAllowance => Text.Get("connection_setup", LongerTest ? "longer_allowance" : "quick_allowance");
    public double TestProgress
    {
        get
        {
            if (_complete)
                return 100;
            if (_timeLimit <= 0)
                return 0;
            return _uploadSeconds > 0 || _phase == ConnectionPhase.Uploading
                ? 50 + Progress(_uploadSeconds) * 50
                : Progress(_downloadSeconds) * 50;
        }
    }
    public string ProgressText => _phase == ConnectionPhase.Idle && TestId.Length == 0
        ? string.Empty
        : Text.Format("units", "percent", TestProgress / 100);
    public string TestDetail => TestId.Length == 0 ? TestAllowance
        : Text.Format("connection_setup", "test_used", Text.Bytes(_downloadBytes + _uploadBytes));
    public string TransferStatus => _phase switch
    {
        ConnectionPhase.Stopping or ConnectionPhase.Downloading or ConnectionPhase.Uploading =>
            Text.Get("connection_setup", "transfers_paused"),
        ConnectionPhase.Holding =>
            Text.Format("connection_setup", "transfers_held", TimeSpan.FromSeconds(_remaining)),
        ConnectionPhase.Completed or ConnectionPhase.Cancelled =>
            Text.Get("connection_setup", "transfers_restored"),
        _ => string.Empty,
    };

    // The local link describes the Internet route only while no proxy or
    // selected adapter carries torrent traffic.
    private ConnectionLink? Link => HasCustomRoute ? null : _link;
    public bool HasLink => Link is not null;
    private bool LinkBelowAdapter => Link is { AdapterMbps: { } adapter } link && adapter > link.Mbps;
    private bool LinkLimits => Link is { AdapterMbps: not null } link
        && (link.Mbps < DownloadCapacity || link.Mbps < UploadCapacity);
    public bool HasLinkWarning => LinkBelowAdapter || LinkLimits || Link is { Metered: true };
    public string LinkTitle => Text.Get("connection_setup",
        LinkBelowAdapter ? "link_below_adapter"
        : LinkLimits ? "link_limits"
        : Link is { Metered: true } ? "link_metered"
        : "link");
    public string LinkDetail =>
        LinkBelowAdapter || LinkLimits ? Text.Get("connection_setup", "link_check")
        : Link is { Metered: true } ? Text.Get("connection_setup", "metered_check")
        : string.Empty;
    public bool HasAdapterSpeed => Link is { AdapterMbps: not null };
    public string AdapterSpeed => Link is { AdapterMbps: { } adapter } ? LinkSpeed(adapter) : string.Empty;
    public string CurrentLinkSpeed => Link is { } link ? LinkSpeed(link.Mbps) : string.Empty;
    public string TestLabel =>
        Text.Get(
            "connection_setup",
            _phase == ConnectionPhase.Holding ? "retest"
                : _phase == ConnectionPhase.Failed ? "retry"
                : "test"
        );
    public string TestStatus =>
        _testFailure.Length > 0 ? Text.Get("connection_setup", _testFailure)
        : _phase is ConnectionPhase.Holding or ConnectionPhase.Completed
            ? Text.Get("connection_setup", "test_complete")
        : _phase != ConnectionPhase.Idle
            ? Text.Get("connection_setup", "test_" + _phase.ToString().ToLowerInvariant())
        : HasCustomRoute ? Text.Get("connection_setup", "test_route")
        : Text.Get("connection_setup", "test_idle");
    public string DownloadError => CapacityError(Download);
    public string UploadError => CapacityError(Upload);
    public string Message =>
        _failure is { } failure ? Text.Error(failure)
        : IsTesting ? Text.Get("connection_setup", "measuring")
        : Changes() is { } changes
            ? Text.Get("connection_setup", changes.Count > 0 ? "ready" : "unchanged")
        : string.Empty;
    public string PresetName => SavedPreset is { } preset ? Name(preset) : "—";
    public string SavedCapacity =>
        _settings.CapacityDownload.ConfirmedNumber is double download and > 0
        && _settings.CapacityUpload.ConfirmedNumber is double upload and > 0
            ? Text.Format("settings", "caps_summary", LinkSpeed(download), LinkSpeed(upload))
            : "—";
    public string StandardSummary => _settings.StandardSummary;
    public string AlternativeSummary => _settings.AlternativeSummary;
    public IReadOnlyList<string> Hierarchy { get; private set; } = [];
    public string StandardDownload => Text.SpeedLimit(SavedStandard?.Download);
    public string StandardUpload => Text.SpeedLimit(SavedStandard?.Upload);
    public string AlternativeDownload => Text.SpeedLimit(SavedAlternative?.Download);
    public string AlternativeUpload => Text.SpeedLimit(SavedAlternative?.Upload);
    public string StandardText
    {
        get
        {
            if (_standardShare == Unlimited)
                return Name(TransferPreset.FullSpeed);
            var preset = Presets.FirstOrDefault(
                choice => Share(choice) == _standardShare,
                TransferPreset.Custom
            );
            return preset == TransferPreset.Custom
                ? Percent(_standardShare)
                : Text.Format("units", "detail", Name(preset), Percent(_standardShare));
        }
    }
    public string AlternativeText =>
        _alternativeShare == Unlimited
            ? Text.Get("transfer_limits", "unlimited")
            : Percent(_alternativeShare);
    public string StandardRates => Rates(_standardShare);
    public string AlternativeRates => Rates(_alternativeShare);
    public string StandardHint =>
        Text.Get("connection_setup", "standard_hint")
        + (StandardFollows ? string.Empty : " " + Text.Get("connection_setup", "kept_hint"));
    public string AlternativeHint =>
        Text.Get("connection_setup", "alternative_hint")
        + (AlternativeFollows ? string.Empty : " " + Text.Get("connection_setup", "kept_hint"));
    public string StandardUse => InUse(_main.HasStandardLimits);
    public string AlternativeUse => InUse(_main.HasAlternativeLimits);
    public event PropertyChangedEventHandler? PropertyChanged;

    private double Progress(double seconds) => Math.Clamp(seconds / _timeLimit, 0, 1);

    internal async Task RefreshLink()
    {
        var revision = ++_linkRevision;
        var link = await Task.Run(() =>
        {
            try
            {
                var profile = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
                if (profile?.NetworkAdapter is not { } adapter)
                    return null;
                var network = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(
                    item => Guid.TryParse(item.Id, out var id) && id == adapter.NetworkAdapterId);
                if (network is null || network.Speed <= 0)
                    return null;
                // Only an Ethernet adapter has a fixed maximum to compare
                // the negotiated link against.
                var ethernet = adapter.IanaInterfaceType == 6;
                return new ConnectionLink(network.Speed / 1_000_000.0,
                    ethernet
                        ? Math.Max(adapter.InboundMaxBitsPerSecond, adapter.OutboundMaxBitsPerSecond) / 1_000_000.0
                        : null,
                    profile.GetConnectionCost().NetworkCostType
                        is Windows.Networking.Connectivity.NetworkCostType.Fixed
                        or Windows.Networking.Connectivity.NetworkCostType.Variable);
            }
            catch (Exception)
            {
                return null;
            }
        });
        if (revision != _linkRevision)
            return;
        _link = link;
        Refresh();
    }

    public string Download
    {
        get => _download;
        set
        {
            if (_download.Equals(value))
                return;
            _download = value;
            _failure = null;
            Refresh();
        }
    }

    public string Upload
    {
        get => _upload;
        set
        {
            if (_upload.Equals(value))
                return;
            _upload = value;
            _failure = null;
            Refresh();
        }
    }

    public double StandardShare
    {
        get => _standardShare;
        set => Move(ref _standardShare, ref _standardMoved, value);
    }

    public double AlternativeShare
    {
        get => _alternativeShare;
        set => Move(ref _alternativeShare, ref _alternativeMoved, value);
    }

    // The saved values, or null until a snapshot confirms them.
    private Caps? SavedStandard => Pair(_settings.Download, _settings.Upload);
    private Caps? SavedAlternative =>
        Pair(_settings.AlternativeDownload, _settings.AlternativeUpload);
    private QueueLimits? SavedQueue =>
        _settings.ActiveDownloads.ConfirmedNumber is { } downloads
        && _settings.ActiveSeeds.ConfirmedNumber is { } seeds
        && _settings.ActiveTotal.ConfirmedNumber is { } total
            ? new((int)downloads, (int)seeds, (int)total)
            : null;

    private static Caps? Pair(Setting download, Setting upload) =>
        download.ConfirmedNumber is { } downloadBytes && upload.ConfirmedNumber is { } uploadBytes
            ? new((int)downloadBytes, (int)uploadBytes)
            : null;

    private TransferPreset? SavedPreset
    {
        get
        {
            if (
                _settings.CapacityDownload.ConfirmedNumber is not { } download
                || _settings.CapacityUpload.ConfirmedNumber is not { } upload
                || SavedStandard is not { } standard
                || SavedQueue is not { } queue
            )
                return null;
            foreach (var preset in Presets)
                if (
                    CapsAt(Share(preset), download, upload) == standard
                    && Queue(Share(preset)) == queue
                )
                    return preset;
            return TransferPreset.Custom;
        }
    }

    // A slider shows the saved limits rounded to a 5% step, so a pair follows
    // its slider only after the person moves it or changes the capacity.
    private bool CapacityChanged =>
        Parse(Download) != (_settings.CapacityDownload.ConfirmedNumber ?? 0)
        || Parse(Upload) != (_settings.CapacityUpload.ConfirmedNumber ?? 0);
    private bool StandardFollows => _standardMoved || CapacityChanged;
    private bool AlternativeFollows => _alternativeMoved || CapacityChanged;
    private bool NeedsCapacity =>
        StandardFollows && _standardShare < Unlimited
        || AlternativeFollows && _alternativeShare < Unlimited;

    public IReadOnlyList<ConnectionChange> ModeRows { get; private set; } = [];
    public IReadOnlyList<ConnectionChange> StandardRows { get; private set; } = [];
    public IReadOnlyList<ConnectionChange> AlternativeRows { get; private set; } = [];
    public IReadOnlyList<ConnectionChange> QueueRows { get; private set; } = [];

    private IReadOnlyList<ConnectionChange> CreateModeRows(Proposal? proposal) =>
        proposal?.EnablesLimits == true
            ?
            [
                new(
                    Text.Get("transfer_limits", "control"),
                    Text.Get("transfer_limits", "none"),
                    Text.Get("settings", "speed"),
                    true
                ),
            ]
            : [];

    private IReadOnlyList<ConnectionChange> CreateQueueRows(QueueLimits? queue)
    {
        var saved = SavedQueue;
        return
        [
            Row(
                Text.Get("settings", "active_downloads"),
                saved?.Downloads,
                queue?.Downloads,
                Count
            ),
            Row(Text.Get("settings", "active_seeds"), saved?.Seeds, queue?.Seeds, Count),
            Row(Text.Get("settings", "active_total"), saved?.Total, queue?.Total, Count),
        ];
    }

    public void Open()
    {
        _download = Input(_settings.CapacityDownload);
        _upload = Input(_settings.CapacityUpload);
        _standardShare = SavedShare(SavedStandard, Share(TransferPreset.Balanced));
        _alternativeShare = SavedShare(SavedAlternative, AlternativeDefault);
        _standardMoved = false;
        _alternativeMoved = false;
        _measuredId = TestId;
        _failure = null;
        Refresh();
    }

    internal void Observe(JsonElement value)
    {
        var sessionId = value.GetProperty("session_id").GetString() ?? string.Empty;
        var sequence = value.GetProperty("sequence").GetInt64();
        if (_sessionId == sessionId && sequence < _sequence)
            return;
        _sessionId = sessionId;
        _sequence = sequence;
        var previousPhase = _phase;
        _phase = value.GetProperty("phase").GetString() switch
        {
            "stopping" => ConnectionPhase.Stopping,
            "downloading" => ConnectionPhase.Downloading,
            "uploading" => ConnectionPhase.Uploading,
            "holding" => ConnectionPhase.Holding,
            "restoring" => ConnectionPhase.Restoring,
            "completed" => ConnectionPhase.Completed,
            "cancelled" => ConnectionPhase.Cancelled,
            "failed" => ConnectionPhase.Failed,
            _ => ConnectionPhase.Idle,
        };
        if (value.TryGetProperty("id", out var id))
        {
            TestId = id.GetString() ?? string.Empty;
            DownloadRate = value.GetProperty("download").GetDouble();
            UploadRate = value.GetProperty("upload").GetDouble();
            _downloadBytes = value.GetProperty("download_bytes").GetDouble();
            _uploadBytes = value.GetProperty("upload_bytes").GetDouble();
            _downloadSeconds = value.GetProperty("download_seconds").GetDouble();
            _uploadSeconds = value.GetProperty("upload_seconds").GetDouble();
            _timeLimit = value.GetProperty("time_limit").GetDouble();
            _complete = value.GetProperty("complete").GetBoolean();
            _remaining = value.GetProperty("remaining").GetInt32();
            var previousFailure = _testFailure;
            _testFailure = value.GetProperty("failure").GetString() ?? string.Empty;
            if (_testFailure == "connection_test_restore_failed" && previousFailure != _testFailure)
                _main.Notifications.Report(
                    new IOException(Text.Get("connection_setup", _testFailure))
                );
            if (
                (_phase is ConnectionPhase.Holding or ConnectionPhase.Completed)
                && _complete
                && TestId != _measuredId
            )
            {
                _measuredId = TestId;
                _download = DownloadRate.ToString("0.##", CultureInfo.CurrentCulture);
                _upload = UploadRate.ToString("0.##", CultureInfo.CurrentCulture);
            }
        }
        else
        {
            TestId = string.Empty;
            DownloadRate = UploadRate = 0;
            _downloadBytes = _uploadBytes = _downloadSeconds = _uploadSeconds = 0;
            _complete = false;
            _testFailure = string.Empty;
        }
        Refresh();
        if (
            _phase != previousPhase
            && _phase != ConnectionPhase.Idle
            && _testFailure != "connection_test_restore_failed"
        )
            _main.Notifications.Announce(TestStatus);
    }

    public async Task Test()
    {
        if (!CanTest)
            return;
        _testFailure = string.Empty;
        await Execute(() => SendTest("start", Requester.Person));
    }

    public async Task CancelTest()
    {
        if (CanCancelTest)
            await Execute(() => SendTest("cancel", Requester.Person));
    }

    public async Task Resume()
    {
        if (IsHolding && !IsPending)
            await Execute(() => SendTest("release", Requester.Person));
    }

    internal async Task<bool> Depart()
    {
        if (IsPending)
            return false;
        if (CanRelease && !await Execute(() => SendTest("release", Requester.Window)))
            return false;
        return true;
    }

    private async Task SendTest(string action, Requester requester)
    {
        var session = _sessionId;
        var reply = await _client.Send(
            "connection_test",
            new { action, profile = LongerTest ? "longer" : "quick" },
            requester
        );
        if (session == _sessionId)
            Observe(reply);
    }

    private async Task<bool> Execute(Func<Task> operation)
    {
        IsPending = true;
        _failure = null;
        _settings.Changed(nameof(Editor.IsPending));
        try
        {
            await operation();
            return true;
        }
        catch (Exception failure)
        {
            _failure = failure;
            return false;
        }
        finally
        {
            IsPending = false;
            _settings.Changed(nameof(Editor.IsPending));
        }
    }

    public async Task<bool> Apply()
    {
        if (!CanEdit || _settings.IsPending)
            return false;
        var changes = Changes();
        if (changes is null || changes.Count == 0)
            return false;
        return await Execute(async () =>
        {
            try
            {
                await _settings.Save(changes);
            }
            finally
            {
                if (CanRelease)
                    await SendTest("release", Requester.Window);
            }
        });
    }

    internal void Refresh()
    {
        var hierarchy = new[]
        {
            Text.Get("settings", "limits"),
            Text.Get("connection_setup", "title"),
        };
        if (!Hierarchy.SequenceEqual(hierarchy))
            Hierarchy = hierarchy;
        var proposal = Propose();
        ModeRows = RetainRows(ModeRows, CreateModeRows(proposal));
        StandardRows = RetainRows(StandardRows, PairRows(SavedStandard, proposal?.Standard));
        AlternativeRows = RetainRows(
            AlternativeRows,
            PairRows(SavedAlternative, proposal?.Alternative)
        );
        QueueRows = RetainRows(QueueRows, CreateQueueRows(proposal?.Queue));
        RefreshMeterScale();
        PropertyChanged?.Invoke(this, new(string.Empty));
    }

    // Both meters share one scale, so equal speeds fill equal arcs. It only
    // grows during a test, so the needles do not jump, and fits the result
    // once the test completes, so a stale entered capacity does not stay.
    private void RefreshMeterScale()
    {
        if (_scaleTestId != TestId || TestId.Length == 0)
        {
            _scaleTestId = TestId;
            MeterScale = Ceiling(DownloadCapacity, UploadCapacity);
        }
        var reading = Ceiling(DownloadRate, UploadRate);
        if (HasResult || reading > (MeterScale ?? 0))
            MeterScale = reading;
    }

    /// <summary>The smallest 1, 2 or 5 step of a power of ten that holds the
    /// largest positive value, or null when no value is positive.</summary>
    private static double? Ceiling(params double[] values)
    {
        var largest = values.Where(value => double.IsFinite(value) && value > 0).DefaultIfEmpty().Max();
        if (largest <= 0)
            return null;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(largest)));
        var step = largest / magnitude;
        return (step <= 1 ? 1 : step <= 2 ? 2 : step <= 5 ? 5 : 10) * magnitude;
    }

    private static IReadOnlyList<ConnectionChange> RetainRows(
        IReadOnlyList<ConnectionChange> current,
        IReadOnlyList<ConnectionChange> next
    ) => current.SequenceEqual(next) ? current : next;

    private Dictionary<string, object>? Changes()
    {
        if (Propose() is not { } proposal)
            return null;
        var changes = new Dictionary<string, object>();
        Add(_settings.Download, proposal.Standard.Download);
        Add(_settings.Upload, proposal.Standard.Upload);
        Add(_settings.AlternativeDownload, proposal.Alternative.Download);
        Add(_settings.AlternativeUpload, proposal.Alternative.Upload);
        Add(_settings.ActiveDownloads, proposal.Queue.Downloads);
        Add(_settings.ActiveSeeds, proposal.Queue.Seeds);
        Add(_settings.ActiveTotal, proposal.Queue.Total);
        Add(_settings.CapacityDownload, Parse(Download));
        Add(_settings.CapacityUpload, Parse(Upload));
        if (proposal.EnablesLimits)
            changes.Add("limit_mode", "speed");
        return changes;

        void Add(Setting setting, double value)
        {
            if (setting.ConfirmedNumber == value)
                return;
            if (setting.Kind == SettingKind.Number)
                changes.Add(setting.Name, value);
            else
                changes.Add(setting.Name, (int)value);
        }
    }

    private void Move(ref int share, ref bool moved, double value)
    {
        var next = (int)Math.Round(value);
        if (next == share)
            return;
        share = next;
        moved = true;
        _failure = null;
        Refresh();
    }

    // Before the first setup no capacity is saved, so the slider starts at
    // its default share. A limited pair stays below the top position, which
    // means no limit.
    private int SavedShare(Caps? saved, int initial)
    {
        var capacity = _settings.CapacityDownload.ConfirmedNumber ?? 0;
        if (capacity <= 0 || saved is not { } caps)
            return initial;
        if (caps.Download == 0)
            return Unlimited;
        return Math.Clamp((int)Math.Round(caps.Download / (capacity * 125000) * 20) * 5, 5, 95);
    }

    // Nothing is proposed until the saved values are known, because each
    // change is measured against them.
    private Proposal? Propose()
    {
        if (
            SavedStandard is not { } savedStandard
            || SavedAlternative is not { } savedAlternative
            || SavedQueue is not { } savedQueue
        )
            return null;
        var download = Parse(Download);
        var upload = Parse(Upload);
        var optional = !NeedsCapacity;
        if (!IsCapacity(download, optional) || !IsCapacity(upload, optional))
            return null;
        var standard = StandardFollows ? CapsAt(_standardShare, download, upload) : savedStandard;
        var alternative = AlternativeFollows
            ? CapsAt(_alternativeShare, download, upload)
            : savedAlternative;
        if (standard is not { } nextStandard || alternative is not { } nextAlternative)
            return null;
        // A new install starts with No limits, so Apply turns on the standard
        // limits it sets. Any other choice is the person's and stays.
        var enables =
            _main.FixedLimits == LimitProfile.None
            && nextStandard != savedStandard
            && nextStandard is not { Download: 0, Upload: 0 };
        return new(
            nextStandard,
            nextAlternative,
            _standardMoved ? Queue(_standardShare) : savedQueue,
            enables
        );
    }

    private IReadOnlyList<ConnectionChange> PairRows(Caps? saved, Caps? next) =>
        [
            Row(
                Text.Get("limits", "download_limit"),
                saved?.Download,
                next?.Download,
                bytes => Text.SpeedLimit(bytes)
            ) with { Glyph = Syno.Lucide.Download },
            Row(
                Text.Get("limits", "upload_limit"),
                saved?.Upload,
                next?.Upload,
                bytes => Text.SpeedLimit(bytes)
            ) with { Glyph = Syno.Lucide.Upload },
        ];

    private static ConnectionChange Row(
        string label,
        int? current,
        int? next,
        Func<int, string> display
    ) =>
        new(
            label,
            current is { } saved ? display(saved) : "—",
            next is { } value ? display(value) : "—",
            next is { } changed && changed != current
        );

    private string InUse(bool inUse) =>
        _main.IsConnected && inUse ? " · " + Text.Get("connection_setup", "in_use") : string.Empty;

    private string LinkSpeed(double mbps) =>
        mbps >= 1000
            ? Text.Format("units", "speed", mbps / 1000, Text.Get("units", "gbps"))
            : Text.Format("units", "speed", mbps, Text.Get("units", "mbps"));

    private string Count(int number) =>
        number == 0
            ? Text.Get("transfer_limits", "unlimited")
            : number.ToString("N0", CultureInfo.CurrentCulture);

    private string Rates(int share)
    {
        if (CapsAt(share, Parse(Download), Parse(Upload)) is not { } caps)
            return Text.Get("connection_setup", "enter_capacity");
        if (share == Unlimited)
            return Text.Get("transfer_limits", "unlimited");
        return Text.Format(
            "settings",
            "caps_summary",
            Text.SpeedLimit(caps.Download),
            Text.SpeedLimit(caps.Upload)
        );
    }

    private string Percent(int share) => Text.Format("units", "percent", share / 100.0);

    private string Name(TransferPreset preset) =>
        Text.Get(
            "connection_setup",
            preset switch
            {
                TransferPreset.Reduced => "reduced",
                TransferPreset.Balanced => "balanced",
                TransferPreset.FullSpeed => "full",
                _ => "custom",
            }
        );

    private string CapacityError(string input)
    {
        if (IsCapacity(Parse(input), optional: !NeedsCapacity))
            return string.Empty;
        return string.IsNullOrWhiteSpace(input)
            ? Text.Get("connection_setup", "required")
            : Text.Format("connection_setup", "invalid", MinimumCapacity, MaximumCapacity);
    }

    private static bool IsCapacity(double value, bool optional)
    {
        if (optional && value == 0)
            return true;
        return double.IsFinite(value) && value >= MinimumCapacity && value <= MaximumCapacity;
    }

    private static double Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return 0;
        return Editor.TryNumber(input, out var number) ? number : double.NaN;
    }

    private static string Input(Setting setting)
    {
        return setting.ConfirmedNumber is double number and > 0
            ? number.ToString("G", CultureInfo.CurrentCulture)
            : string.Empty;
    }

    private static int Share(TransferPreset preset) =>
        preset switch
        {
            TransferPreset.Reduced => 50,
            TransferPreset.Balanced => 85,
            _ => Unlimited,
        };

    private static QueueLimits Queue(int share) =>
        share <= Share(TransferPreset.Reduced) ? new(2, 2, 3) : new(3, 5, 8);

    // Capacity is in megabits a second, so 1 Mbps is 125,000 bytes a second.
    private static Caps? CapsAt(int share, double download, double upload)
    {
        if (share == Unlimited)
            return new(0, 0);
        if (!IsCapacity(download, optional: false) || !IsCapacity(upload, optional: false))
            return null;
        var fraction = share / 100.0;
        return new(
            (int)Math.Round(download * 125000 * fraction),
            (int)Math.Round(upload * 125000 * fraction)
        );
    }

    private readonly record struct Caps(int Download, int Upload);

    private sealed record ConnectionLink(double Mbps, double? AdapterMbps, bool Metered);

    private readonly record struct QueueLimits(int Downloads, int Seeds, int Total);

    private sealed record Proposal(
        Caps Standard,
        Caps Alternative,
        QueueLimits Queue,
        bool EnablesLimits
    );
}

public sealed record ConnectionChange(
    string Label,
    string Current,
    string Proposed,
    bool IsChanged
)
{
    // The direction icon of a speed limit row.
    public string Glyph { get; init; } = string.Empty;
}
