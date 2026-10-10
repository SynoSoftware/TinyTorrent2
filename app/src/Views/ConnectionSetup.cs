using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Views;

public sealed class ConnectionSetup : INotifyPropertyChanged
{
    private const double MinimumCapacity = 0.01;
    private const double MaximumCapacity = int.MaxValue / 125000.0;
    private readonly Settings _settings;
    private readonly MainViewModel _main;
    private readonly PipeClient _client;
    private ConnectionPhase _phase;
    private string _measuredId = string.Empty;
    private string _testFailure = string.Empty;
    private int _remaining;

    internal ConnectionSetup(Settings settings, MainViewModel main, PipeClient client)
    {
        _settings = settings;
        _main = main;
        _client = client;
    }

    private string _download = string.Empty;
    private string _upload = string.Empty;
    private TransferPreset _preset = TransferPreset.Balanced;
    private Exception? _failure;
    public Strings Text => _settings.Text;
    public bool IsPending { get; private set; }
    public bool CanLeave => !IsPending;
    public bool CanEdit => _settings.CanEdit && !IsPending && !IsTesting && _phase != ConnectionPhase.Restoring;
    public bool CanApply => CanEdit && !_settings.IsPending && Changes() is { Count: > 0 };
    public bool IsTesting => _phase is ConnectionPhase.Stopping or ConnectionPhase.Downloading or ConnectionPhase.Uploading;
    private bool CanRelease => _main.IsConnected && (IsTesting || _phase == ConnectionPhase.Holding);
    private bool HasCustomRoute => _settings.Proxy.IsInUse || _settings.Adapter.ConfirmedText.Length > 0;
    public bool CanTest => CanEdit && !_settings.IsPending && !HasCustomRoute;
    public bool CanCancelTest => _main.IsConnected && IsTesting && !IsPending;
    public string TestLabel => Text.Get("connection_setup", _phase == ConnectionPhase.Holding ? "retest"
        : _phase == ConnectionPhase.Failed ? "retry" : "test");
    public string TestStatus => _testFailure.Length > 0 ? Text.Get("connection_setup", _testFailure)
        : _phase == ConnectionPhase.Holding ? Text.Format("connection_setup", "holding", _remaining)
        : _phase != ConnectionPhase.Idle ? Text.Get("connection_setup", "test_" + _phase.ToString().ToLowerInvariant())
        : HasCustomRoute ? Text.Get("connection_setup", "test_route")
        : Text.Get("connection_setup", "test_idle");
    public string DownloadError => CapacityError(Download);
    public string UploadError => CapacityError(Upload);
    public string Message
    {
        get
        {
            if (_failure is { } failure)
                return Text.Error(failure);
            if (Proposal is null)
                return string.Empty;
            return Text.Get("connection_setup", CanApply ? "ready" : "no_changes");
        }
    }
    public string PresetName => Name(CurrentPreset);
    public event PropertyChangedEventHandler? PropertyChanged;

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

    public int PresetIndex
    {
        get => (int)_preset;
        set
        {
            if (value is < 0 or > 2 || value == (int)_preset)
                return;
            _preset = (TransferPreset)value;
            _failure = null;
            Refresh();
        }
    }

    private Limits? Proposal => Propose(_preset, Parse(Download), Parse(Upload));
    private TransferPreset CurrentPreset
    {
        get
        {
            var download = _settings.CapacityDownload.ConfirmedNumber;
            var upload = _settings.CapacityUpload.ConfirmedNumber;
            foreach (var preset in new[]
                { TransferPreset.Reduced, TransferPreset.Balanced, TransferPreset.FullSpeed })
                if (Propose(preset, download, upload) is { } proposal && Matches(proposal))
                    return preset;
            return TransferPreset.Custom;
        }
    }

    public IReadOnlyList<ConnectionChange> Rows
    {
        get
        {
            var proposal = Proposal;
            var rows = new List<ConnectionChange>
            {
                Row(_settings.Download, proposal?.DownloadCap),
                Row(_settings.Upload, proposal?.UploadCap),
                Row(_settings.ActiveDownloads, proposal?.Downloads),
                Row(_settings.ActiveSeeds, proposal?.Seeds),
                Row(_settings.ActiveTotal, proposal?.Total),
                Row(_settings.ConnectionLimit, (int)_settings.ConnectionLimit.ConfirmedNumber),
                Row(_settings.TorrentConnections, (int)_settings.TorrentConnections.ConfirmedNumber),
                CapacityRow(_settings.CapacityDownload, Download, "download_capacity"),
                CapacityRow(_settings.CapacityUpload, Upload, "upload_capacity"),
            };
            var mode = _main.FixedLimits ?? LimitMode.Schedule;
            var proposedMode = _main.FixedLimits is null ? LimitMode.Schedule : LimitMode.Speed;
            rows.Add(
                new(
                    Text.Get("connection_setup", "mode"),
                    _main.LimitLabel(mode),
                    _main.LimitLabel(proposedMode),
                    mode != proposedMode
                )
            );
            return rows;
        }
    }

    public void Open()
    {
        _download = Input(_settings.CapacityDownload);
        _upload = Input(_settings.CapacityUpload);
        var preset = CurrentPreset;
        _preset = preset == TransferPreset.Custom ? TransferPreset.Balanced : preset;
        _failure = null;
        Refresh();
    }

    internal void Observe(JsonElement value)
    {
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
        _client.SetConnectionTestActive(IsTesting || _phase is ConnectionPhase.Holding or ConnectionPhase.Restoring);
        if (value.TryGetProperty("id", out var id))
        {
            var testId = id.GetString() ?? string.Empty;
            _remaining = value.GetProperty("remaining").GetInt32();
            var previousFailure = _testFailure;
            _testFailure = value.GetProperty("failure").GetString() ?? string.Empty;
            if (_testFailure == "connection_test_restore_failed" && previousFailure != _testFailure)
                _main.Report(new IOException(Text.Get("connection_setup", _testFailure)));
            if ((_phase is ConnectionPhase.Holding or ConnectionPhase.Completed) && testId != _measuredId
                && value.GetProperty("download").GetDouble() > 0 && value.GetProperty("upload").GetDouble() > 0)
            {
                _measuredId = testId;
                _download = value.GetProperty("download").GetDouble().ToString("0.##", CultureInfo.CurrentCulture);
                _upload = value.GetProperty("upload").GetDouble().ToString("0.##", CultureInfo.CurrentCulture);
            }
        }
        else
            _testFailure = string.Empty;
        Refresh();
        if (_phase != previousPhase && _phase != ConnectionPhase.Idle
            && _testFailure != "connection_test_restore_failed")
            _main.Announce(TestStatus);
    }

    public async Task Test()
    {
        if (!CanTest)
            return;
        _testFailure = string.Empty;
        _client.SetConnectionTestActive(true);
        await Execute(() => SendTest("start"));
    }

    public async Task CancelTest()
    {
        if (CanCancelTest)
            await Execute(() => SendTest("cancel"));
    }

    internal async Task<bool> Depart()
    {
        if (IsPending)
            return false;
        if (CanRelease && !await Execute(() => SendTest("release")))
            return false;
        return true;
    }

    private async Task SendTest(string action) =>
        Observe(await _client.Send("connection_test", new { action }));

    private async Task<bool> Execute(Func<Task> operation)
    {
        IsPending = true;
        _failure = null;
        _settings.Changed(nameof(Settings.IsPending));
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
            _main.RequestSnapshot();
            IsPending = false;
            _settings.Changed(nameof(Settings.IsPending));
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
                    await SendTest("release");
            }
        });
    }

    internal void Refresh() => PropertyChanged?.Invoke(this, new(string.Empty));

    private Dictionary<string, object>? Changes()
    {
        if (Proposal is not { } proposal)
            return null;
        var changes = new Dictionary<string, object>();
        Add(_settings.Download, proposal.DownloadCap);
        Add(_settings.Upload, proposal.UploadCap);
        Add(_settings.ActiveDownloads, proposal.Downloads);
        Add(_settings.ActiveSeeds, proposal.Seeds);
        Add(_settings.ActiveTotal, proposal.Total);
        Add(_settings.CapacityDownload, Parse(Download));
        Add(_settings.CapacityUpload, Parse(Upload));
        if (_main.FixedLimits is { } mode && mode != LimitMode.Speed)
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

    private bool Matches(Limits proposal) =>
        _settings.Download.ConfirmedNumber == proposal.DownloadCap
        && _settings.Upload.ConfirmedNumber == proposal.UploadCap
        && _settings.ActiveDownloads.ConfirmedNumber == proposal.Downloads
        && _settings.ActiveSeeds.ConfirmedNumber == proposal.Seeds
        && _settings.ActiveTotal.ConfirmedNumber == proposal.Total;

    private ConnectionChange Row(Setting setting, int? value)
    {
        var current = (int)setting.ConfirmedNumber;
        return new(
            setting.IsRate ? Text.Get("connection_setup", setting.Name) : setting.Label,
            Display(current),
            value is { } number ? Display(number) : "—",
            value is { } next && next != current
        );

        string Display(int number)
        {
            if (number == 0)
                return Text.Get("transfer_limits", "unlimited");
            if (setting.IsRate)
                return Text.Format("units", "rate", Text.Bytes(number));
            return number.ToString("N0", CultureInfo.CurrentCulture);
        }
    }

    private ConnectionChange CapacityRow(Setting setting, string input, string label)
    {
        var current = setting.ConfirmedNumber;
        var number = Parse(input);
        var proposed = IsCapacity(number, optional: true) ? number : current;
        return new(
            Text.Get("connection_setup", label),
            Display(current),
            Display(proposed),
            current != proposed
        );

        string Display(double value) =>
            value > 0
                ? Text.Format(
                    "connection_setup", "capacity_value", value.ToString("G", CultureInfo.CurrentCulture)
                )
                : "—";
    }

    private string Name(TransferPreset preset) =>
        Text.Get("connection_setup", preset switch
        {
            TransferPreset.Reduced => "reduced",
            TransferPreset.Balanced => "balanced",
            TransferPreset.FullSpeed => "full",
            _ => "custom",
        });

    private string CapacityError(string input)
    {
        if (IsCapacity(Parse(input), optional: _preset == TransferPreset.FullSpeed))
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
        return Settings.TryNumber(input, out var number) ? number : double.NaN;
    }

    private static string Input(Setting setting)
    {
        var number = setting.ConfirmedNumber;
        return number > 0 ? number.ToString("G", CultureInfo.CurrentCulture) : string.Empty;
    }

    private static Limits? Propose(TransferPreset preset, double download, double upload)
    {
        var uncapped = preset == TransferPreset.FullSpeed;
        if (!IsCapacity(download, optional: uncapped) || !IsCapacity(upload, optional: uncapped))
            return null;
        var reduced = preset == TransferPreset.Reduced;
        var fraction = reduced ? 0.5 : 0.85;
        return new(
            DownloadCap: uncapped ? 0 : (int)Math.Round(download * 125000 * fraction),
            UploadCap: uncapped ? 0 : (int)Math.Round(upload * 125000 * fraction),
            Downloads: reduced ? 2 : 3,
            Seeds: reduced ? 2 : 5,
            Total: reduced ? 3 : 8
        );
    }

    private sealed record Limits(int DownloadCap, int UploadCap, int Downloads, int Seeds, int Total);
}

public sealed record ConnectionChange(string Label, string Current, string Proposed, bool IsChanged);
