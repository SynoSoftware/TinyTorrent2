using System.Text.Json;

namespace Syno.TinyTorrent.Shell;

public sealed partial class MainViewModel
{
    // The limits and the pause the engine applies, as its last snapshot
    // reported them.
    private sealed record AppliedLimits(
        LimitOrigin Origin,
        LimitProfile Profile,
        PauseReason Pause,
        int DownloadCap,
        int UploadCap
    )
    {
        internal LimitMode Mode =>
            Origin == LimitOrigin.Schedule
                ? LimitMode.Schedule
                : Profile switch
                {
                    LimitProfile.None => LimitMode.None,
                    LimitProfile.Speed => LimitMode.Speed,
                    LimitProfile.Alternative => LimitMode.Alternative,
                    _ => throw new InvalidOperationException(),
                };
    }

    // Null while no engine runs.
    private AppliedLimits? _limits;

    // The restriction last applied, so a change that happens on its own, such as
    // the schedule pausing transfers, reaches Narrator.
    private (Restriction Kind, PauseReason Pause)? _restriction;
    private (LimitMode Mode, long? Sequence)? _limitsChoice;
    private long _limitsSequence;

    private bool CanChooseLimits => CanEdit && _limitsChoice is not { Sequence: null };
    private bool HasSchedule => _limits is { Origin: not LimitOrigin.Manual };

    // The limits chosen for the whole week, or null while the weekly schedule
    // decides them or the choice is not known. The page follows the engine's
    // choice, not one still being applied, so it changes once per choice.
    internal LimitProfile? FixedLimits =>
        _limits is { Origin: not LimitOrigin.Schedule } limits ? limits.Profile : null;
    public bool FollowsSchedule => FixedLimits is null;
    public bool CanUseNone => CanChoose(LimitMode.None);
    public bool CanUseSpeed => CanChoose(LimitMode.Speed);
    public bool CanUseAlternative => CanChoose(LimitMode.Alternative);
    public bool CanFollowSchedule => CanChoose(LimitMode.Schedule);

    // The chooser shows a choice still being applied in place of the current one.
    public int LimitsIndex =>
        _limits is { } limits ? (int)(_limitsChoice?.Mode ?? limits.Mode) : -1;

    internal bool HasStandardLimits => _limits is { Profile: LimitProfile.Speed };
    public bool HasAlternativeLimits => _limits is { Profile: LimitProfile.Alternative };
    internal PauseReason PausedBy => _limits?.Pause ?? PauseReason.None;

    // What holds transfers back, which the status bar shows as one item: a
    // pause, or else the alternative limits, which change nothing while paused.
    private Restriction Restriction =>
        PausedBy != PauseReason.None ? Restriction.Paused
        : HasAlternativeLimits ? Restriction.Alternative
        : Restriction.None;
    public bool IsSessionPaused => Restriction == Restriction.Paused;

    // Resume all lifts the person's and the schedule's pause, not a missing adapter's.
    internal bool IsPausedByChoice =>
        PausedBy is not (PauseReason.None or PauseReason.Adapter or PauseReason.ConnectionTest);

    public bool HasRestriction => Restriction != Restriction.None;

    public string RestrictionStatus =>
        Restriction switch
        {
            Restriction.Paused when PausedBy == PauseReason.Adapter
                => Text.Get("window", "adapter_pause"),
            Restriction.Paused => PauseStatus,
            _ => AlternativeStatus,
        };
    internal bool HasCaps =>
        Restriction != Restriction.Paused
        && _limits is { } limits
        && (limits.DownloadCap > 0 || limits.UploadCap > 0);
    public string RestrictionTip => IsSessionPaused ? PauseStatus : AlternativeTip;
    public bool ShowsAlternativeLimits => Restriction == Restriction.Alternative;
    private string AlternativeStatus =>
        HasSchedule && Settings.Schedule.NextChange(DateTime.Now) is { } change
            ? Text.Format("window", "alternative_until", change)
            : Text.Get("window", "alternative");
    public string AlternativeTip =>
        _limits is { } limits
            ? Text.Format(
                "transfer_limits",
                "alternative_tip",
                Text.SpeedLimit(limits.DownloadCap),
                Text.SpeedLimit(limits.UploadCap),
                Text.Get(
                    "transfer_limits",
                    "origin_" + limits.Origin.ToString().ToLowerInvariant()
                )
            )
            : string.Empty;
    public string LimitsHeading =>
        _limits?.Profile switch
        {
            null => "—",
            LimitProfile.Speed => Text.Get("settings", "speed"),
            LimitProfile.Alternative => Text.Get("settings", "alternative"),
            _ => Text.Get("transfer_limits", "none"),
        };
    public string LimitsSummary =>
        _limits is not { } limits
            ? "—"
            : LimitsHeading
                + " · "
                + FormatCaps("settings", "caps_summary")
                + (limits.Mode == LimitMode.Schedule ? " · " + LimitsNow : string.Empty);
    public string ScheduleSummary =>
        _limits is null ? "—"
        : FixedLimits is { } mode ? Text.Format("settings", "schedule_unused", LimitLabel(mode))
        : Settings.Schedule.NextChange(DateTime.Now) is { } change
            ? Text.Format("settings", "schedule_until", change)
        : Text.Get("settings", "schedule_all_week");
    public string DownloadCap => Text.SpeedLimit(_limits?.DownloadCap);
    public string UploadCap => Text.SpeedLimit(_limits?.UploadCap);
    public string DownloadStatus => RateStatus(_downloadRate, _limits?.DownloadCap);
    public string UploadStatus => RateStatus(_uploadRate, _limits?.UploadCap);
    public string DownloadTip => RateTip("download_rate", _downloadRate, _limits?.DownloadCap);
    public string UploadTip => RateTip("upload_rate", _uploadRate, _limits?.UploadCap);
    public string PauseStatus =>
        PausedBy switch
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
            if (_limits is not { } limits)
                return string.Empty;
            if (FixedLimits is { } mode)
                return Text.Format("transfer_limits", "now_fixed", LimitLabel(mode));
            var state = Text.Get(
                "transfer_limits",
                "state_"
                    + (limits.Pause == PauseReason.Schedule ? "paused" : EngineName(limits.Profile))
            );
            return Settings.Schedule.NextChange(DateTime.Now) is { } change
                ? Text.Format("transfer_limits", "now_until", state, change)
                : Text.Format("transfer_limits", "now", state);
        }
    }

    // A cap means nothing while transfers are paused, so the rate shows alone.
    private string RateStatus(double rate, int? cap) =>
        cap > 0 && !IsSessionPaused
            ? Text.Format("window", "rate_cap", Rate(rate), Text.SpeedLimit(cap))
            : Rate(rate);

    private string RateTip(string key, double rate, int? cap)
    {
        var current = Text.Format("window", key, Rate(rate));
        if (_limits is not { } limits)
            return current;
        if (limits.Profile == LimitProfile.None)
            return current + "\n" + Text.Get("transfer_limits", "unlimited");
        var pair = Text.Get("window", HasAlternativeLimits ? "alternative_pair" : "speed_pair");
        return current
            + "\n"
            + (
                cap > 0
                    ? Text.Format("window", "rate_limit", Text.SpeedLimit(cap), pair)
                    : Text.Format("window", "no_rate_limit", pair)
            );
    }

    private string LimitLabel(LimitProfile profile) =>
        Text.Get("transfer_limits", "state_" + EngineName(profile));

    private static string EngineName(LimitProfile profile) =>
        profile switch
        {
            LimitProfile.None => "none",
            LimitProfile.Speed => "speed",
            LimitProfile.Alternative => "alternative",
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        };

    // The engine's name for a mode; following the schedule is the absence of one.
    private static string? EngineName(LimitMode mode) =>
        mode switch
        {
            LimitMode.None => EngineName(LimitProfile.None),
            LimitMode.Speed => EngineName(LimitProfile.Speed),
            LimitMode.Alternative => EngineName(LimitProfile.Alternative),
            _ => null,
        };

    private bool CanChoose(LimitMode mode) =>
        CanEdit && (CanChooseLimits || _limitsChoice?.Mode == mode);

    // A message naming the applied download and upload caps, in that order.
    internal string FormatCaps(string group, string key) =>
        Text.Format(
            group,
            key,
            Text.SpeedLimit(_limits?.DownloadCap),
            Text.SpeedLimit(_limits?.UploadCap)
        );

    private void ApplyLimits(JsonElement limits, long sequence)
    {
        _limitsSequence = sequence;
        if (_limitsChoice is { Sequence: { } confirmed } && sequence >= confirmed)
            _limitsChoice = null;
        var fixedLimits = FixedLimits;
        _limits = new(
            Origin: limits.GetProperty("origin").GetString() switch
            {
                "manual" => LimitOrigin.Manual,
                "override" => LimitOrigin.Override,
                "schedule" => LimitOrigin.Schedule,
                _ => throw new InvalidDataException("Unknown limits source."),
            },
            Profile: limits.GetProperty("mode").GetString() switch
            {
                "none" => LimitProfile.None,
                "speed" => LimitProfile.Speed,
                "alternative" => LimitProfile.Alternative,
                _ => throw new InvalidDataException("Unknown limits mode."),
            },
            Pause: limits.GetProperty("pause").GetString() switch
            {
                "" => PauseReason.None,
                "adapter" => PauseReason.Adapter,
                "manual" => PauseReason.Manual,
                "schedule" => PauseReason.Schedule,
                "connection_test" => PauseReason.ConnectionTest,
                _ => throw new InvalidDataException("Unknown pause reason."),
            },
            DownloadCap: limits.GetProperty("download").GetInt32(),
            UploadCap: limits.GetProperty("upload").GetInt32()
        );
        // The week draws a fixed choice in place of its periods.
        if (FixedLimits != fixedLimits)
            Settings.Schedule.Refresh();
        // The toast after Resume all explains only a missing adapter's pause.
        if (PausedBy == PauseReason.None || _waiting is [] && MissingAdapter.Length == 0)
            _waiting = null;
        AnnounceRestriction();
    }

    // Compares the kind of restriction, not its text, because the time in the
    // text changes at midnight without anything else changing.
    private void AnnounceRestriction()
    {
        (Restriction Kind, PauseReason Pause) restriction = (Restriction, PausedBy);
        var previous = _restriction;
        _restriction = restriction;
        if (previous is not { } was || was == restriction)
            return;
        Notifications.Announce(
            HasRestriction
                ? RestrictionTip
                : Text.Get(
                    "window",
                    was.Kind == Restriction.Paused ? "resumed_status" : "alternative_off"
                )
        );
    }

    public async Task ChooseLimits(LimitMode mode)
    {
        if (
            !CanChooseLimits
            || _limits is not { } limits
            || mode == (_limitsChoice?.Mode ?? limits.Mode)
        )
            return;
        var session = _sessionId;
        _limitsChoice = (mode, null);
        RefreshWindow();
        try
        {
            // A fixed choice holds until the weekly schedule is chosen again,
            // so it turns the schedule off instead of overriding it.
            var follows = mode == LimitMode.Schedule;
            var sequence = await Settings.Save(
                new { schedule_enabled = follows, limit_mode = EngineName(mode) }
            );
            _limitsChoice =
                _connected && _sessionId == session && _limitsSequence < sequence
                    ? (mode, sequence)
                    : null;
        }
        catch (Exception error)
        {
            _limitsChoice = null;
            Notifications.Report(error);
        }
        finally
        {
            RefreshWindow();
        }
    }
}
