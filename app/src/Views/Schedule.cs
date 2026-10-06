using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Input;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Views;

public sealed class Schedule : INotifyPropertyChanged
{
    private readonly Preferences _owner;
    private Exception? _scheduleError;
    private bool _savingSchedule;
    private int? _editingIndex;
    private SchedulePeriod[]? _submittedSchedule;
    private PeriodDraft? _draft;
    private bool _invalidPeriod;
    private bool _duplicatePeriod;
    public SchedulePeriod? Selection { get; private set; }

    public ObservableCollection<SchedulePeriod> Periods { get; } = [];
    public PeriodDraft? Draft => _draft;
    public bool HasDraft => _draft?.HasChanges == true;
    public bool IsPending => _savingSchedule;
    public bool CanEdit => _owner.CanEdit;
    public bool CanSchedule => CanEdit && !_savingSchedule;
    public bool IsEditing => _draft is not null;
    public bool HasPeriods => Periods.Count > 0;
    public bool HasAlternative => Periods.Any(period => period.Mode == ScheduleMode.Alternative);
    public bool HasPaused => Periods.Any(period => period.Mode == ScheduleMode.Paused);
    public string PeriodSummary => Text.Format("preferences", "period_count", Periods.Count);
    public string EditorTitle => Text.Get("preferences", _editingIndex is null ? "new_period" : "edit_title");
    public string WeekStatus => Text.Get("preferences", Enabled.IsOn ? "schedule_active" : "schedule_inactive");
    public string AlternativeSummary => Text.Format("preferences", "period_group", FormatMode(ScheduleMode.Alternative), Periods.Count(period => period.Mode == ScheduleMode.Alternative));
    public string PausedSummary => Text.Format("preferences", "period_group", FormatMode(ScheduleMode.Paused), Periods.Count(period => period.Mode == ScheduleMode.Paused));
    public bool ShowsSelection => Selection is not null && !IsEditing;
    internal SchedulePeriod? Preview => _draft is { Start: not null, End: not null } draft && draft.Days.Any(day => day.IsChecked)
        ? new SchedulePeriod(this, draft) : null;
    public bool HasScheduleError => _scheduleError is not null || _invalidPeriod || _duplicatePeriod;
    public string ScheduleMessage => _duplicatePeriod ? Text.Get("preferences", "duplicate_period") :
        _invalidPeriod ? Text.Get("preferences", "invalid_period") :
        _scheduleError is null ? string.Empty : Text.Error(_scheduleError);
    public Strings Text => _owner.Text;
    public Preference Enabled { get; }
    public string OnText => _owner.OnText;
    public string OffText => _owner.OffText;
    public ICommand AddPeriod { get; }
    public ICommand SavePeriod { get; }
    public ICommand CancelPeriod { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? TextChanged;
    public event EventHandler? WeekChanged;

    internal Schedule(Preferences owner)
    {
        _owner = owner;
        Enabled = new(owner, "schedule_enabled", PreferenceKind.Boolean, PreferenceSection.Schedule);
        AddPeriod = new Command(() => { Edit(null); return Task.CompletedTask; }, () => CanSchedule && !IsEditing);
        SavePeriod = new Command(CommitPeriod, () => CanSchedule && IsEditing);
        CancelPeriod = new Command(() => { CancelDraft(); return Task.CompletedTask; }, () => IsEditing && !_savingSchedule);
    }

    public Task Toggle(bool value) => _owner.Toggle(Enabled, value);

    internal void Apply(JsonElement schedule)
    {
        var values = schedule.EnumerateArray().Select(value => new SchedulePeriod(this, value)).ToArray();
        if (!_savingSchedule && _submittedSchedule is { } submitted && values.Length == submitted.Length &&
            values.Where((period, index) => !period.Matches(submitted[index])).Any() == false)
        {
            if (_draft is { } draft)
            {
                _editingIndex ??= submitted.Length - 1;
                if (draft.Start is not null && draft.End is not null && new SchedulePeriod(this, draft).Matches(submitted[_editingIndex.Value]))
                    CancelDraft();
            }
            _submittedSchedule = null;
        }
        if (Periods.Count == values.Length && !Periods.Where((period, index) => !period.Matches(values[index])).Any()) return;
        Selection = values.FirstOrDefault(period => Selection is { } selected && period.Matches(selected));
        Periods.Clear();
        foreach (var period in values) Periods.Add(period);
        Refresh();
    }

    internal void Edit(SchedulePeriod? period)
    {
        if (!CanSchedule || IsEditing) return;
        if (period is not null && !Periods.Contains(period)) return;
        BeginEdit(period);
        _scheduleError = null;
        _invalidPeriod = false;
        _duplicatePeriod = false;
        Refresh();
    }

    private void BeginEdit(SchedulePeriod? period)
    {
        _editingIndex = period is null ? null : Periods.IndexOf(period);
        _draft = new PeriodDraft(this, period);
        Selection = period;
    }

    internal void Select(SchedulePeriod? period)
    {
        if (IsEditing || (period is not null && !Periods.Contains(period))) return;
        Selection = period;
        Refresh();
    }

    internal void CreatePeriod(int day, PeriodSpan span)
    {
        if (!CanSchedule || IsEditing) return;
        Edit(null);
        if (_draft is not { } draft) return;
        foreach (var choice in draft.Days) choice.IsChecked = choice.Index == day;
        draft.SetSpan(span);
    }

    internal async Task Reschedule(SchedulePeriod period, PeriodSpan span)
    {
        if (!CanSchedule || IsEditing || !Periods.Contains(period) || period.Span == span) return;
        var changed = period.WithSpan(span);
        if (await SubmitPeriod(changed, Periods.IndexOf(period))) return;
        var current = Periods.FirstOrDefault(candidate => candidate.Matches(period) || candidate.Matches(changed));
        if (current is null) return;
        BeginEdit(current);
        _draft?.SetSpan(span);
        Refresh();
    }

    public async Task<bool> CommitPeriod()
    {
        if (_draft is not { } draft) return true;
        if (!_owner.CanSave || _savingSchedule) return false;
        if (!draft.Days.Any(day => day.IsChecked) || draft.Start is null || draft.End is null)
        {
            _invalidPeriod = true;
            Refresh();
            return false;
        }
        var period = new SchedulePeriod(this, draft);
        if (!await SubmitPeriod(period, _editingIndex)) return false;
        CancelDraft();
        return true;
    }

    private async Task<bool> SubmitPeriod(SchedulePeriod period, int? index)
    {
        _duplicatePeriod = Periods.Where((_, candidate) => candidate != index).Any(value => value.Matches(period));
        if (_duplicatePeriod) { Refresh(); return false; }
        var periods = Periods.ToList();
        if (index is { } position) periods[position] = period;
        else periods.Add(period);
        if (!await SubmitSchedule(periods)) return false;
        Selection = Periods[index ?? (Periods.Count - 1)];
        Refresh();
        return true;
    }

    internal async Task Remove(SchedulePeriod period)
    {
        if (!CanSchedule || IsEditing) return;
        await SubmitSchedule(Periods.Where(value => value != period).ToArray());
    }

    private async Task<bool> SubmitSchedule(IEnumerable<SchedulePeriod> periods)
    {
        _savingSchedule = true;
        _scheduleError = null;
        _invalidPeriod = false;
        _duplicatePeriod = false;
        Refresh();
        try
        {
            _submittedSchedule = periods.ToArray();
            var schedule = _submittedSchedule.Select(period => new { days = period.Days, start = period.Start, end = period.End,
                mode = period.Mode.ToString().ToLowerInvariant() }).ToArray();
            await _owner.Save(new { schedule });
            Apply(JsonSerializer.SerializeToElement(schedule));
            _submittedSchedule = null;
            return true;
        }
        catch (Exception error) { _scheduleError = error; return false; }
        finally { _savingSchedule = false; Refresh(); }
    }

    public void CancelDraft()
    {
        _draft = null;
        _editingIndex = null;
        _submittedSchedule = null;
        _scheduleError = null;
        _invalidPeriod = false;
        _duplicatePeriod = false;
        Refresh();
    }

    internal void RefreshText()
    {
        Refresh();
        _draft?.Refresh();
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        foreach (var period in Periods) period.Refresh();
        foreach (Command command in new[] { AddPeriod, SavePeriod, CancelPeriod }) command.Refresh();
        WeekChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void RefreshDraft()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasDraft)));
        WeekChanged?.Invoke(this, EventArgs.Empty);
    }

    internal IEnumerable<ScheduleRange> Ranges(int day, SchedulePeriod? original = null, SchedulePeriod? preview = null)
    {
        var periods = Periods.Select(period => period == original ? preview ?? period : period);
        if (original is null && preview is not null) periods = periods.Append(preview);
        var spans = periods.SelectMany(period => period.Occurrences(day).Select(span =>
            new ScheduleRange(Math.Max(0, span.Start), Math.Min(1440, span.End), period.Mode, period))).ToArray();
        var boundaries = new SortedSet<int> { 0, 1440 };
        foreach (var span in spans) { boundaries.Add(span.Start); boundaries.Add(span.End); }
        var points = boundaries.ToArray();
        ScheduleRange? previous = null;
        for (var index = 0; index < points.Length - 1; index++)
        {
            var minute = points[index];
            var active = spans.Where(span => minute >= span.Start && minute < span.End);
            var period = (active.FirstOrDefault(span => span.Mode == ScheduleMode.Paused) ?? active.FirstOrDefault())?.Period;
            var mode = period?.Mode ?? ScheduleMode.Normal;
            if (previous is not null && previous.Period == period) previous = previous with { End = points[index + 1] };
            else
            {
                if (previous is not null) yield return previous;
                previous = new ScheduleRange(points[index], points[index + 1], mode, period);
            }
        }
        if (previous is not null) yield return previous;
    }

    internal string Day(int index) => Text.Get("preferences", new[] { "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday" }[index]);
    internal string ShortDay(int index) => Text.Get("preferences", new[] { "monday_short", "tuesday_short", "wednesday_short", "thursday_short", "friday_short", "saturday_short", "sunday_short" }[index]);
    internal string FormatMode(ScheduleMode mode) => Text.Get("preferences", mode.ToString().ToLowerInvariant());
    internal string DescribeDays(IReadOnlyList<int> days)
    {
        if (days.Count == 7) return Text.Get("preferences", "every_day");
        var groups = new List<string>();
        for (var index = 0; index < days.Count; index++)
        {
            var start = days[index];
            while (index + 1 < days.Count && days[index + 1] == days[index] + 1) index++;
            groups.Add(start == days[index] ? ShortDay(start) : Text.Format("preferences", "day_range", ShortDay(start), ShortDay(days[index])));
        }
        return string.Join(", ", groups);
    }
    internal static string Time(int minutes) => DateTime.Today.AddMinutes(minutes).ToString("t", CultureInfo.CurrentCulture);
}

public sealed class SchedulePeriod : INotifyPropertyChanged
{
    private readonly Schedule _owner;
    public IReadOnlyList<int> Days { get; } = [];
    public int Start { get; }
    public int End { get; }
    public ScheduleMode Mode { get; }
    internal PeriodSpan Span => new(Start, End > Start ? End - Start : 1440 - Start + End);
    public string Summary => _owner.Text.Format("preferences", "period_summary", _owner.DescribeDays(Days), TimeLabel);
    public string TimeRange => Start == 0 && End == 0 ? _owner.Text.Get("preferences", "time_all_day") :
        _owner.Text.Format("preferences", End <= Start ? "time_overnight" : "time_range",
            Schedule.Time(Start), Schedule.Time(End));
    public string TimeLabel => _owner.Text.Format("preferences", "time_summary", TimeRange,
        Span.Duration < 60 ? _owner.Text.Format("preferences", "duration_minutes", Span.Duration) :
        Span.Duration % 60 == 0 ? _owner.Text.Format("preferences", "duration_hours", Span.Duration / 60) :
        _owner.Text.Format("preferences", "duration_both", Span.Duration / 60, Span.Duration % 60));
    public string ModeLabel => _owner.FormatMode(Mode);
    public string Description => Start == 0 && End == 0
        ? _owner.Text.Format("preferences", "period_all_day", _owner.DescribeDays(Days), _owner.FormatMode(Mode))
        : _owner.Text.Format("preferences", End <= Start ? "period_overnight" : "period", _owner.DescribeDays(Days),
            Schedule.Time(Start), Schedule.Time(End), _owner.FormatMode(Mode));
    public string EditText => _owner.Text.Get("preferences", "edit");
    public string RemoveText => _owner.Text.Get("preferences", "remove");
    public string EditName => _owner.Text.Format("preferences", "edit_period", Description);
    public string RemoveName => _owner.Text.Format("preferences", "remove_period", Description);
    public ICommand Edit { get; }
    public ICommand Remove { get; }
    public event PropertyChangedEventHandler? PropertyChanged;

    private SchedulePeriod(Schedule owner)
    {
        _owner = owner;
        Edit = new Command(() => { owner.Edit(this); return Task.CompletedTask; }, () => owner.CanSchedule && !owner.IsEditing);
        Remove = new Command(() => owner.Remove(this), () => owner.CanSchedule && !owner.IsEditing);
    }
    internal SchedulePeriod(Schedule owner, JsonElement value) : this(owner)
    {
        Days = value.GetProperty("days").EnumerateArray().Select(day => day.GetInt32()).Order().ToArray();
        Start = value.GetProperty("start").GetInt32();
        End = value.GetProperty("end").GetInt32();
        Mode = value.GetProperty("mode").GetString() switch
        {
            "paused" => ScheduleMode.Paused,
            "alternative" => ScheduleMode.Alternative,
            _ => throw new InvalidDataException("Invalid schedule mode.")
        };
    }
    internal SchedulePeriod(Schedule owner, PeriodDraft draft) : this(owner)
    {
        Days = draft.Days.Where(day => day.IsChecked).Select(day => day.Index).ToArray();
        Start = (int)draft.Start.GetValueOrDefault().TotalMinutes;
        End = (int)draft.End.GetValueOrDefault().TotalMinutes;
        Mode = draft.IsPaused ? ScheduleMode.Paused : ScheduleMode.Alternative;
    }
    internal SchedulePeriod(Schedule owner, IReadOnlyList<int> days, PeriodSpan span, ScheduleMode mode) : this(owner)
    {
        Days = days;
        Start = span.Start;
        End = span.End;
        Mode = mode;
    }
    internal SchedulePeriod WithSpan(PeriodSpan span) => new(_owner, Days, span, Mode);
    internal IEnumerable<ScheduleRange> Occurrences(int day)
    {
        if (Days.Contains(day)) yield return new(Start, Start + Span.Duration, Mode, this);
        if (Start + Span.Duration > 1440 && Days.Contains((day + 6) % 7))
            yield return new(Start - 1440, Start + Span.Duration - 1440, Mode, this);
    }
    internal void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        ((Command)Edit).Refresh();
        ((Command)Remove).Refresh();
    }
    internal bool Matches(SchedulePeriod period) => Days.SequenceEqual(period.Days) && Start == period.Start && End == period.End && Mode == period.Mode;
}

public sealed class PeriodDraft : INotifyPropertyChanged
{
    private readonly Schedule _owner;
    private readonly SchedulePeriod _original;
    private TimeSpan? _start;
    private TimeSpan? _end;
    private bool _paused;
    public DayChoice[] Days { get; }
    public TimeSpan? Start
    {
        get => _start;
        set { if (_start == value) return; _start = value; Changed(); }
    }
    public TimeSpan? End
    {
        get => _end;
        set { if (_end == value) return; _end = value; Changed(); }
    }
    public bool IsPaused
    {
        get => _paused;
        set { if (_paused == value) return; _paused = value; Changed(); }
    }
    public bool HasChanges => Start?.TotalMinutes != _original.Start || End?.TotalMinutes != _original.End ||
        IsPaused != (_original.Mode == ScheduleMode.Paused) || !Days.Where(day => day.IsChecked).Select(day => day.Index).SequenceEqual(_original.Days);
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void SetSpan(PeriodSpan span)
    {
        var start = TimeSpan.FromMinutes(span.Start);
        var end = TimeSpan.FromMinutes(span.End);
        if (_start == start && _end == end) return;
        _start = start;
        _end = end;
        Changed();
    }

    internal void Changed()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        _owner.RefreshDraft();
    }

    public string TimeLabel => Start is not null && End is not null ? new SchedulePeriod(_owner, this).TimeLabel : string.Empty;

    internal PeriodDraft(Schedule owner, SchedulePeriod? period)
    {
        _owner = owner;
        _original = period ?? new SchedulePeriod(owner, [0, 1, 2, 3, 4, 5, 6], new PeriodSpan(9 * 60, 8 * 60), ScheduleMode.Alternative);
        Days = Enumerable.Range(0, 7).Select(index => new DayChoice(this, index,
            _original.Days.Contains(index))).ToArray();
        _start = TimeSpan.FromMinutes(_original.Start);
        _end = TimeSpan.FromMinutes(_original.End);
        _paused = _original.Mode == ScheduleMode.Paused;
    }
    internal string Day(int index) => _owner.Day(index);
    internal void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        foreach (var day in Days) day.Refresh();
    }
}

public sealed class DayChoice(PeriodDraft owner, int index, bool isChecked) : INotifyPropertyChanged
{
    private bool _checked = isChecked;
    public int Index { get; } = index;
    public string Label => owner.Day(Index);
    public string AutomationId => "PeriodDay" + Index;
    public bool IsChecked
    {
        get => _checked;
        set { if (_checked == value) return; _checked = value; Refresh(); owner.Changed(); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

internal sealed record ScheduleRange(int Start, int End, ScheduleMode Mode, SchedulePeriod? Period);
