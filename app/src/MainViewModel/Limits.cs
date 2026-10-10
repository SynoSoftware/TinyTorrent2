using System.Text.Json;
using Syno.TinyTorrent.Models;

namespace Syno.TinyTorrent;

public sealed partial class MainViewModel
{
    private LimitOrigin _limitOrigin;

    // What the engine applies now: None, Speed or Alternative, never Schedule.
    private LimitMode _appliedLimits;
    private PauseReason _pause;

    // The restriction last applied, so a change that happens on its own, such as
    // the schedule pausing transfers, reaches Narrator.
    private (bool Paused, PauseReason Reason, bool Alternative)? _restriction;
    private int _downloadCap;
    private int _uploadCap;
    private LimitMode? _limitsChoice;
    private Exception? _limitsError;

    private bool HasLimits => _connected && !_loading && !_storageFailed;
    private LimitMode CurrentLimits =>
        _limitOrigin == LimitOrigin.Schedule ? LimitMode.Schedule : _appliedLimits;
    private bool CanChooseLimits => CanEdit && _limitsChoice is null;
    private bool HasSchedule => HasLimits && _limitOrigin != LimitOrigin.Manual;

    // The limits chosen for the whole week, or null while the weekly schedule
    // decides them or the choice is not known. The page follows the engine's
    // choice, not one still being applied, so it changes once per choice.
    internal LimitMode? FixedLimits =>
        HasLimits && CurrentLimits != LimitMode.Schedule ? CurrentLimits : null;
    public bool FollowsSchedule => FixedLimits is null;
    public bool CanUseNone => CanChoose(LimitMode.None);
    public bool CanUseSpeed => CanChoose(LimitMode.Speed);
    public bool CanUseAlternative => CanChoose(LimitMode.Alternative);
    public bool CanFollowSchedule => CanChoose(LimitMode.Schedule);

    // The chooser shows a choice still being applied in place of the current one.
    public int LimitsIndex => HasLimits ? (int)(_limitsChoice ?? CurrentLimits) : -1;

    // A pair stays editable while it applies or will apply again when the
    // schedule takes over; under None with no schedule, neither does.
    public bool CanEditSpeed => CanEdit && (CurrentLimits == LimitMode.Speed || HasSchedule);
    public bool CanEditAlternative =>
        CanEdit
        && (
            CurrentLimits == LimitMode.Alternative
            || HasSchedule && Settings.Schedule.HasAlternative
        );
    public bool HasAlternativeLimits => HasLimits && _appliedLimits == LimitMode.Alternative;
    internal PauseReason PausedBy => _pause;

    // Resume all lifts the person's and the schedule's pause, not a missing adapter's.
    internal bool IsPausedByChoice => IsPaused && _pause is not (PauseReason.Adapter or PauseReason.ConnectionTest);

    // What holds transfers back, which the status bar shows as one item: a
    // pause, or else the alternative limits, which change nothing while paused.
    public bool HasRestriction => IsSessionPaused || HasAlternativeLimits;

    // The connection item already names a missing adapter, so this one says only
    // that transfers are paused.
    public string RestrictionStatus =>
        !IsSessionPaused ? AlternativeStatus
        : _pause == PauseReason.Adapter ? Text.Get("status", "paused")
        : PauseStatus;
    public string RestrictionTip => IsSessionPaused ? PauseStatus : AlternativeTip;
    public string RestrictionGlyph => IsSessionPaused ? Syno.Lucide.Pause : Syno.Lucide.Turtle;
    private string AlternativeStatus =>
        HasSchedule && Settings.Schedule.NextChange(DateTime.Now) is { } change
            ? Text.Format("window", "alternative_until", change)
            : Text.Get("window", "alternative");
    public string AlternativeTip =>
        Text.Format(
            "transfer_limits",
            "alternative_tip",
            Cap(_downloadCap),
            Cap(_uploadCap),
            Text.Get("transfer_limits", "origin_" + _limitOrigin.ToString().ToLowerInvariant())
        );
    public string LimitsMessage =>
        _limitsError is not null ? Text.Error(_limitsError) : string.Empty;
    public string SpeedUse => InUse(HasLimits && _appliedLimits == LimitMode.Speed);
    public string AlternativeUse => InUse(HasAlternativeLimits);
    public string DownloadStatus => RateStatus(_downloadRate, _downloadCap);
    public string UploadStatus => RateStatus(_uploadRate, _uploadCap);
    public string DownloadTip => RateTip("download_rate", _downloadRate, _downloadCap);
    public string UploadTip => RateTip("upload_rate", _uploadRate, _uploadCap);
    public string PauseStatus =>
        _pause switch
        {
            PauseReason.Schedule => Settings.Schedule.NextChange(DateTime.Now) is { } change
                ? Text.Format("status", "scheduled_until", change)
                : Text.Get("status", "scheduled"),
            PauseReason.Adapter => Text.Format("window", "no_adapter", MissingAdapter),
            PauseReason.Manual => Text.Get("status", "all_paused"),
            PauseReason.ConnectionTest => Text.Get("connection_setup", "paused"),
            _ => string.Empty,
        };
    public string LimitsNow
    {
        get
        {
            if (!HasLimits)
                return string.Empty;
            if (FixedLimits is { } mode)
                return Text.Format(
                    "transfer_limits",
                    "now_fixed",
                    LimitLabel(mode)
                );
            var state = Text.Get(
                "transfer_limits",
                "state_" + (_pause == PauseReason.Schedule ? "paused" : EngineName(_appliedLimits))
            );
            return Settings.Schedule.NextChange(DateTime.Now) is { } change
                ? Text.Format("transfer_limits", "now_until", state, change)
                : Text.Format("transfer_limits", "now", state);
        }
    }

    private string InUse(bool inUse) =>
        inUse ? " · " + Text.Get("transfer_limits", "in_use") : string.Empty;

    // A cap means nothing while transfers are paused, so the rate shows alone.
    private string RateStatus(double rate, int cap) =>
        HasLimits && cap > 0 && !IsSessionPaused
            ? Text.Format("window", "rate_cap", Rate(rate), Cap(cap))
            : Rate(rate);

    private string RateTip(string key, double rate, int cap)
    {
        var current = Text.Format("window", key, Rate(rate));
        if (!HasLimits)
            return current;
        if (_appliedLimits == LimitMode.None)
            return current + "\n" + Text.Get("transfer_limits", "unlimited");
        var pair = Text.Get("window", HasAlternativeLimits ? "alternative_pair" : "speed_pair");
        return current
            + "\n"
            + (
                cap > 0
                    ? Text.Format("window", "rate_limit", Cap(cap), pair)
                    : Text.Format("window", "no_rate_limit", pair)
            );
    }

    internal string LimitLabel(LimitMode mode) =>
        mode == LimitMode.Schedule
            ? Text.Get("settings", "schedule")
            : Text.Get("transfer_limits", "state_" + EngineName(mode));

    // The engine's name for a mode; following the schedule is the absence of one.
    private static string? EngineName(LimitMode mode) =>
        mode switch
        {
            LimitMode.None => "none",
            LimitMode.Speed => "speed",
            LimitMode.Alternative => "alternative",
            _ => null,
        };

    private bool CanChoose(LimitMode mode) =>
        CanEdit && (_limitsChoice is null || _limitsChoice == mode);

    // A message naming the applied download and upload caps, in that order.
    internal string FormatCaps(string group, string key) =>
        Text.Format(group, key, Cap(_downloadCap), Cap(_uploadCap));

    private string Cap(int value) =>
        !HasLimits ? "—"
        : value == 0 ? Text.Get("transfer_limits", "unlimited")
        : Text.Format("units", "rate", Text.Bytes(value));

    private void ApplyLimits(JsonElement limits)
    {
        var fixedLimits = FixedLimits;
        _limitOrigin = limits.GetProperty("origin").GetString() switch
        {
            "manual" => LimitOrigin.Manual,
            "override" => LimitOrigin.Override,
            "schedule" => LimitOrigin.Schedule,
            _ => throw new InvalidDataException("Unknown limits source."),
        };
        var mode = limits.GetProperty("mode").GetString();
        _appliedLimits =
            Enum.GetValues<LimitMode>()
                .Where(choice => choice != LimitMode.Schedule && EngineName(choice) == mode)
                .Select(choice => (LimitMode?)choice)
                .SingleOrDefault()
            ?? throw new InvalidDataException("Unknown limits mode.");
        _downloadCap = limits.GetProperty("download").GetInt32();
        _uploadCap = limits.GetProperty("upload").GetInt32();
        _pause = limits.GetProperty("pause").GetString() switch
        {
            "" => PauseReason.None,
            "adapter" => PauseReason.Adapter,
            "manual" => PauseReason.Manual,
            "schedule" => PauseReason.Schedule,
            "connection_test" => PauseReason.ConnectionTest,
            _ => throw new InvalidDataException("Unknown pause reason."),
        };
        // The week draws a fixed choice in place of its periods.
        if (FixedLimits != fixedLimits)
            Settings.Schedule.Refresh();
        // The notice after Resume all explains only a missing adapter's pause.
        if (_pause == PauseReason.None || _waiting is [] && MissingAdapter.Length == 0)
            _waiting = null;
        AnnounceRestriction();
    }

    // Compares the kind of restriction, not its text, because the time in the
    // text changes at midnight without anything else changing.
    private void AnnounceRestriction()
    {
        var restriction = (IsSessionPaused, _pause, HasAlternativeLimits && !IsSessionPaused);
        var previous = _restriction;
        _restriction = restriction;
        if (previous is not { } was || was == restriction)
            return;
        Announce(
            HasRestriction
                ? RestrictionTip
                : Text.Get("window", was.Paused ? "resumed_status" : "alternative_off")
        );
    }

    public async Task ChooseLimits(LimitMode mode)
    {
        if (!CanChooseLimits || mode == CurrentLimits)
            return;
        _limitsChoice = mode;
        _limitsError = null;
        RefreshWindow();
        try
        {
            // A fixed choice holds until the weekly schedule is chosen again,
            // so it turns the schedule off instead of overriding it.
            var follows = mode == LimitMode.Schedule;
            await Settings.Save(new { schedule_enabled = follows, limit_mode = EngineName(mode) });
            await _client.Read(Consumer.Summary, "snapshot");
        }
        catch (Exception error)
        {
            _limitsError = error;
            Announce(Text.Error(error));
        }
        finally
        {
            _limitsChoice = null;
            RefreshWindow();
        }
    }
}
