using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Views;

public sealed class Schedule : INotifyPropertyChanged
{
    private readonly Settings _owner;
    private Task? _saving;
    private Task? _changing;
    private (int Index, PeriodDraft Draft)? _open;
    private Refusal? _refusal;

    public ReadOnlyCollection<SchedulePeriod> Periods { get; private set; } = new([]);
    public PeriodDraft? Draft => _open?.Draft;
    public SchedulePeriod? OpenPeriod =>
        _open is { } open && open.Index < Periods.Count ? Periods[open.Index] : null;

    // The open period holds input the schedule does not hold yet: a change
    // being saved, or one that failed.
    public bool HasDraft =>
        Draft is { } draft && OpenPeriod is { } open && draft.Period?.Matches(open) != true;
    public bool IsPending => _saving is not null || _changing is not null;
    public bool CanEdit => _owner.CanEdit;
    public bool CanSchedule => CanEdit && !IsPending;
    public bool IsOpen => _open is not null;
    public bool HasAlternative => Periods.Any(period => period.Mode == ScheduleMode.Alternative);
    public bool HasScheduleError => _refusal is not null;
    public string DaysMessage =>
        _refusal?.Reason == RefusalReason.InvalidPeriod
            ? Text.Get("settings", "invalid_period")
            : string.Empty;
    public string ScheduleMessage =>
        _refusal switch
        {
            { Reason: RefusalReason.DuplicatePeriod } => Text.Get("settings", "duplicate_period"),
            { Failure: { } failure } => Text.Error(failure),
            _ => string.Empty,
        };
    public string PeriodMessage => IsOpen ? ScheduleMessage : string.Empty;
    public string PeriodsMessage => IsOpen ? string.Empty : ScheduleMessage;
    public Strings Text => _owner.Text;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? TextChanged;

    private sealed record Refusal(RefusalReason Reason, Exception? Failure = null);

    internal Schedule(Settings owner) => _owner = owner;

    internal void Apply(JsonElement schedule)
    {
        var values = schedule
            .EnumerateArray()
            .Select(value => new SchedulePeriod(this, value))
            .ToArray();
        if (
            Periods.Count == values.Length
            && Periods.Zip(values).All(pair => pair.First.Matches(pair.Second))
        )
            return;
        // Adding or removing a period invalidates the open period's position.
        if (Periods.Count != values.Length)
        {
            _open = null;
            _refusal = null;
        }
        Periods = Array.AsReadOnly(values);
        Refresh();
    }

    internal Task Open(SchedulePeriod period)
    {
        var index = Periods.IndexOf(period);
        if (index < 0 || (period == OpenPeriod && !IsPending))
            return Task.CompletedTask;
        var count = Periods.Count;
        return ChangePeriod(() =>
        {
            if (count == Periods.Count && _open?.Index != index)
                Edit(index);
            return Task.CompletedTask;
        });
    }

    private void Edit(int index)
    {
        _open = (index, new PeriodDraft(this, Periods[index]));
        _refusal = null;
        Refresh();
    }

    public Task Close() =>
        ChangePeriod(() =>
        {
            ClearDraft();
            return Task.CompletedTask;
        });

    internal Task Close(SchedulePeriod period)
    {
        if (!IsPending && period != OpenPeriod)
            return Task.CompletedTask;
        var index = Periods.IndexOf(period);
        if (index < 0)
            return Task.CompletedTask;
        var count = Periods.Count;
        return ChangePeriod(() =>
        {
            if (count == Periods.Count && _open?.Index == index)
                ClearDraft();
            return Task.CompletedTask;
        });
    }

    private void ClearDraft()
    {
        if (_open is null)
            return;
        _open = null;
        _refusal = null;
        Refresh();
    }

    internal SchedulePeriod NewPeriod(IReadOnlyList<int> days, PeriodSpan span) =>
        new(this, days, span, ScheduleMode.Alternative);

    internal Task Add() => Create(NewPeriod([0, 1, 2, 3, 4], new PeriodSpan(9 * 60, 8 * 60)));

    internal Task CreatePeriod(int day, PeriodSpan span) => Create(NewPeriod([day], span));

    // A new period saves at once and opens. When the schedule already holds
    // the same period, that one opens instead.
    private Task Create(SchedulePeriod period)
    {
        if (!CanEdit)
            return Task.CompletedTask;
        return ChangePeriod(async () =>
        {
            if (!_owner.CanSave)
                return;
            ClearDraft();
            var created = Periods.FirstOrDefault(value => value.Matches(period));
            if (created is null)
            {
                if (!await SubmitPeriod(period, null))
                    return;
                created = Periods[^1];
            }
            Edit(Periods.IndexOf(created));
        });
    }

    internal async Task Reschedule(SchedulePeriod period, PeriodSpan span)
    {
        if (!CanSchedule)
            return;
        await Open(period);
        if (OpenPeriod == period)
            Draft?.SetSpan(span);
    }

    // Each change to the open period applies at once. A change made during a
    // save applies when that save ends.
    internal async Task ApplyDraft()
    {
        while (_saving is null && Draft is { } draft && OpenPeriod is { } open)
        {
            _refusal = null;
            if (draft.Period is not { } period)
            {
                _refusal = new(RefusalReason.InvalidPeriod);
                Refresh();
                return;
            }
            if (period.Matches(open))
            {
                Refresh();
                return;
            }
            if (!await SubmitPeriod(period, Periods.IndexOf(open)))
                return;
        }
    }

    // Leaving Settings keeps a period the schedule did not take on screen
    // with its error, so the person never leaves believing it runs; a
    // duplicate closes because the schedule already holds it. A departure
    // waits for the save in progress and applies a change made during it, so
    // a refusal never lands on a hidden page.
    internal async Task<bool> Depart()
    {
        await Settle();
        if (!HasDraft || !_owner.CanSave)
            return true;
        if (_refusal?.Reason == RefusalReason.DuplicatePeriod)
        {
            ClearDraft();
            return true;
        }
        return _refusal?.Reason != RefusalReason.InvalidPeriod
            && _refusal?.Failure is not CommandException;
    }

    private async Task Settle()
    {
        // Closing or switching periods must drain valid input typed during a
        // save before the editor that holds it is replaced.
        while (true)
        {
            if (_saving is { } saving)
                await saving;
            else if (HasDraft && _refusal is null && _owner.CanSave)
                await ApplyDraft();
            else
                return;
        }
    }

    // Changes run one at a time in the order asked for, so a click made
    // during another change still acts. Period positions survive an edit, but
    // not another Add or Remove.
    private async Task ChangePeriod(Func<Task> change)
    {
        while (_changing is { } running)
            await running;
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _changing = changed.Task;
        Refresh();
        try
        {
            await Settle();
            await change();
        }
        finally
        {
            _changing = null;
            changed.SetResult();
            Refresh();
        }
    }

    private async Task<bool> SubmitPeriod(SchedulePeriod period, int? index)
    {
        if (Periods.Where((_, candidate) => candidate != index).Any(value => value.Matches(period)))
        {
            _refusal = new(RefusalReason.DuplicatePeriod);
            Refresh();
            return false;
        }
        var periods = Periods.ToList();
        if (index is { } position)
            periods[position] = period;
        else
            periods.Add(period);
        return await SubmitSchedule(periods);
    }

    // A save in progress replaces every period, so the period to remove is
    // found again by its position.
    internal Task Remove(SchedulePeriod period)
    {
        if (!CanEdit)
            return Task.CompletedTask;
        var index = Periods.IndexOf(period);
        if (index < 0)
            return Task.CompletedTask;
        var count = Periods.Count;
        return ChangePeriod(async () =>
        {
            if (!_owner.CanSave || count != Periods.Count)
                return;
            ClearDraft();
            await SubmitSchedule(Periods.Where((_, position) => position != index).ToArray());
        });
    }

    private async Task<bool> SubmitSchedule(IEnumerable<SchedulePeriod> periods)
    {
        var saved = new TaskCompletionSource();
        _saving = saved.Task;
        _refusal = null;
        Refresh();
        try
        {
            var schedule = periods
                .Select(period => new
                {
                    days = period.Days,
                    start = period.Start,
                    end = period.End,
                    mode = period.Mode.ToString().ToLowerInvariant(),
                })
                .ToArray();
            await _owner.Save(new { schedule });
            Apply(JsonSerializer.SerializeToElement(schedule));
            return true;
        }
        catch (Exception error)
        {
            _refusal = new(RefusalReason.SaveFailed, error);
            return false;
        }
        finally
        {
            _saving = null;
            saved.SetResult();
            Refresh();
        }
    }

    internal void RefreshText()
    {
        Refresh();
        Draft?.Refresh();
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void Refresh() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    internal IEnumerable<ScheduleRange> Ranges(
        int day,
        SchedulePeriod? original = null,
        SchedulePeriod? preview = null
    )
    {
        var periods = Periods.Select(period => period == original ? preview ?? period : period);
        if (original is null && preview is not null)
            periods = periods.Append(preview);
        var spans = periods
            .SelectMany(period =>
                period
                    .Occurrences(day)
                    .Select(span => new ScheduleRange(
                        Math.Max(0, span.Start),
                        Math.Min(1440, span.End),
                        period.Mode,
                        period
                    ))
            )
            .ToArray();
        var boundaries = new SortedSet<int> { 0, 1440 };
        foreach (var span in spans)
        {
            boundaries.Add(span.Start);
            boundaries.Add(span.End);
        }
        var points = boundaries.ToArray();
        ScheduleRange? previous = null;
        for (var index = 0; index < points.Length - 1; index++)
        {
            var minute = points[index];
            var active = spans.Where(span => minute >= span.Start && minute < span.End);
            var period = (
                active.FirstOrDefault(span => span.Mode == ScheduleMode.Paused)
                ?? active.FirstOrDefault()
            )?.Period;
            var mode = period?.Mode ?? ScheduleMode.Normal;
            if (previous is not null && previous.Period == period)
                previous = previous with { End = points[index + 1] };
            else
            {
                if (previous is not null)
                    yield return previous;
                previous = new ScheduleRange(points[index], points[index + 1], mode, period);
            }
        }
        if (previous is not null)
            yield return previous;
    }

    // When the scheduled mode next changes after the given local time: the
    // time alone when that is today, or null when the mode never changes.
    internal string? NextChange(DateTime now)
    {
        var today = Weekday(now);
        var minute = now.Hour * 60 + now.Minute;
        ScheduleMode? mode = null;
        for (var offset = 0; offset <= 7; offset++)
        {
            var day = (today + offset) % 7;
            foreach (var range in Ranges(day))
            {
                if (offset == 0 && range.End <= minute)
                    continue;
                if (mode is null)
                {
                    mode = range.Mode;
                    continue;
                }
                if (offset == 7 && range.Start > minute)
                    return null;
                if (range.Mode == mode)
                    continue;
                return offset == 0
                    ? Time(range.Start)
                    : Text.Format("transfer_limits", "day_time", ShortDay(day), Time(range.Start));
            }
        }
        return null;
    }

    internal string Day(int index) =>
        Text.Get(
            "settings",
            new[] { "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday" }[
                index
            ]
        );

    internal string ShortDay(int index) =>
        Text.Get(
            "settings",
            new[]
            {
                "monday_short",
                "tuesday_short",
                "wednesday_short",
                "thursday_short",
                "friday_short",
                "saturday_short",
                "sunday_short",
            }[index]
        );

    // Time outside the periods has the standard limits.
    internal string FormatMode(ScheduleMode mode) =>
        Text.Get(
            "settings",
            mode == ScheduleMode.Normal ? "speed" : mode.ToString().ToLowerInvariant()
        );

    internal string DescribeDays(IReadOnlyList<int> days)
    {
        if (days.Count == 7)
            return Text.Get("settings", "every_day");
        var groups = new List<string>();
        for (var index = 0; index < days.Count; index++)
        {
            var start = days[index];
            while (index + 1 < days.Count && days[index + 1] == days[index] + 1)
                index++;
            groups.Add(
                start == days[index]
                    ? ShortDay(start)
                    : Text.Format("settings", "day_range", ShortDay(start), ShortDay(days[index]))
            );
        }
        return string.Join(", ", groups);
    }

    // The schedule numbers days from Monday, as the engine does.
    internal static int Weekday(DateTime time) => ((int)time.DayOfWeek + 6) % 7;

    internal static string Time(int minutes) =>
        DateTime.Today.AddMinutes(minutes).ToString("t", CultureInfo.CurrentCulture);
}

public sealed class SchedulePeriod
{
    private readonly Schedule _owner;
    public IReadOnlyList<int> Days { get; } = [];
    public int Start { get; }
    public int End { get; }
    public ScheduleMode Mode { get; }
    internal PeriodSpan Span => new(Start, End > Start ? End - Start : 1440 - Start + End);
    public string TimeRange =>
        Start == 0 && End == 0
            ? _owner.Text.Get("settings", "time_all_day")
            : _owner.Text.Format(
                "settings",
                End <= Start ? "time_overnight" : "time_range",
                Schedule.Time(Start),
                Schedule.Time(End)
            );
    public string Length =>
        Span.Duration < 60 ? _owner.Text.Format("settings", "duration_minutes", Span.Duration)
        : Span.Duration % 60 == 0
            ? _owner.Text.Format("settings", "duration_hours", Span.Duration / 60)
        : _owner.Text.Format("settings", "duration_both", Span.Duration / 60, Span.Duration % 60);
    public string TimeLabel => _owner.Text.Format("settings", "time_summary", TimeRange, Length);
    public string Description =>
        Start == 0 && End == 0
            ? _owner.Text.Format(
                "settings",
                "period_all_day",
                _owner.DescribeDays(Days),
                _owner.FormatMode(Mode)
            )
            : _owner.Text.Format(
                "settings",
                End <= Start ? "period_overnight" : "period",
                _owner.DescribeDays(Days),
                Schedule.Time(Start),
                Schedule.Time(End),
                _owner.FormatMode(Mode)
            );
    public string RemoveName => _owner.Text.Format("settings", "remove_period", Description);

    private SchedulePeriod(Schedule owner) => _owner = owner;

    internal SchedulePeriod(Schedule owner, JsonElement value)
        : this(owner)
    {
        Days = value
            .GetProperty("days")
            .EnumerateArray()
            .Select(day => day.GetInt32())
            .Order()
            .ToArray();
        Start = value.GetProperty("start").GetInt32();
        End = value.GetProperty("end").GetInt32();
        Mode = value.GetProperty("mode").GetString() switch
        {
            "paused" => ScheduleMode.Paused,
            "alternative" => ScheduleMode.Alternative,
            _ => throw new InvalidDataException("Invalid schedule mode."),
        };
    }

    internal SchedulePeriod(Schedule owner, PeriodDraft draft)
        : this(owner)
    {
        Days = draft.Days.Where(day => day.IsChecked).Select(day => day.Index).ToArray();
        Start = (int)draft.Start.GetValueOrDefault().TotalMinutes;
        End = (int)draft.End.GetValueOrDefault().TotalMinutes;
        Mode = draft.Mode;
    }

    internal SchedulePeriod(
        Schedule owner,
        IReadOnlyList<int> days,
        PeriodSpan span,
        ScheduleMode mode
    )
        : this(owner)
    {
        Days = days;
        Start = span.Start;
        End = span.End;
        Mode = mode;
    }

    internal SchedulePeriod WithSpan(PeriodSpan span) => new(_owner, Days, span, Mode);

    internal IEnumerable<ScheduleRange> Occurrences(int day)
    {
        if (Days.Contains(day))
            yield return new(Start, Start + Span.Duration, Mode, this);
        if (Start + Span.Duration > 1440 && Days.Contains((day + 6) % 7))
            yield return new(Start - 1440, Start + Span.Duration - 1440, Mode, this);
    }

    internal bool Matches(SchedulePeriod period) =>
        Days.SequenceEqual(period.Days)
        && Start == period.Start
        && End == period.End
        && Mode == period.Mode;
}

public sealed class PeriodDraft : INotifyPropertyChanged
{
    private readonly Schedule _owner;
    private TimeSpan? _start;
    private TimeSpan? _end;
    private ScheduleMode _mode;
    public DayChoice[] Days { get; }
    public TimeSpan? Start
    {
        get => _start;
        set
        {
            if (_start == value)
                return;
            _start = value;
            Changed();
        }
    }
    public TimeSpan? End
    {
        get => _end;
        set
        {
            if (_end == value)
                return;
            _end = value;
            Changed();
        }
    }
    internal ScheduleMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
                return;
            _mode = value;
            Changed();
        }
    }
    public int ModeIndex
    {
        get => Mode == ScheduleMode.Paused ? 1 : 0;
        set
        {
            if (value >= 0)
                Mode = value == 1 ? ScheduleMode.Paused : ScheduleMode.Alternative;
        }
    }

    // The period these values describe, or null while a day or a time is missing.
    internal SchedulePeriod? Period =>
        Start is not null && End is not null && Days.Any(day => day.IsChecked)
            ? new SchedulePeriod(_owner, this)
            : null;
    public string Duration =>
        Start is not null && End is not null
            ? _owner.Text.Format("settings", "duration", new SchedulePeriod(_owner, this).Length)
            : string.Empty;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal PeriodDraft(Schedule owner, SchedulePeriod period)
    {
        _owner = owner;
        Days = Enumerable
            .Range(0, 7)
            .Select(index => new DayChoice(this, index, period.Days.Contains(index)))
            .ToArray();
        _start = TimeSpan.FromMinutes(period.Start);
        _end = TimeSpan.FromMinutes(period.End);
        _mode = period.Mode;
    }

    internal void SetSpan(PeriodSpan span)
    {
        var start = TimeSpan.FromMinutes(span.Start);
        var end = TimeSpan.FromMinutes(span.End);
        if (_start == start && _end == end)
            return;
        _start = start;
        _end = end;
        Changed();
    }

    internal void Changed()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        _ = _owner.ApplyDraft();
    }

    internal string Day(int index) => _owner.Day(index);

    internal string ShortDay(int index) => _owner.ShortDay(index);

    internal void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        foreach (var day in Days)
            day.Refresh();
    }
}

public sealed class DayChoice(PeriodDraft owner, int index, bool isChecked) : INotifyPropertyChanged
{
    private bool _checked = isChecked;
    public int Index { get; } = index;
    public string Label => owner.Day(Index);
    public string Abbreviation => owner.ShortDay(Index);
    public string AutomationId => "PeriodDay" + Index;
    public bool IsChecked
    {
        get => _checked;
        set
        {
            if (_checked == value)
                return;
            _checked = value;
            Refresh();
            owner.Changed();
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Refresh() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

internal sealed record ScheduleRange(int Start, int End, ScheduleMode Mode, SchedulePeriod? Period);
