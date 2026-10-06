using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Input;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Views;

public sealed class Preferences : INotifyPropertyChanged
{
    private readonly MainViewModel _owner;
    private readonly PipeClient _client;
    private JsonElement _registration;
    private Exception? _registrationError;
    private Exception? _scheduleError;
    private bool _registering;
    private bool _savingSchedule;
    private int? _editingIndex;
    private SchedulePeriod[]? _submittedSchedule;
    private PeriodDraft? _draft;
    private bool _invalidPeriod;
    private bool _duplicatePeriod;
    public SchedulePeriod? Selection { get; private set; }

    public Strings Text => _owner.Text;
    public Preference Destination { get; }
    public Preference ShowAdd { get; }
    public Preference ShowSplash { get; }
    public Preference StartInTray { get; }
    public Preference Download { get; }
    public Preference Upload { get; }
    public Preference AlternativeDownload { get; }
    public Preference AlternativeUpload { get; }
    public Preference Downloads { get; }
    public Preference Seeds { get; }
    public Preference Connections { get; }
    public Preference Ratio { get; }
    public Preference SeedingMinutes { get; }
    public Preference Interface { get; }
    public Preference PortMapping { get; }
    public Preference Port { get; }
    public Preference Notifications { get; }
    public Preference PreventSleep { get; }
    public Preference SeedingSleep { get; }
    public Preference Updates { get; }
    public Preference Schedule { get; }
    public IReadOnlyList<Preference> Fields { get; }
    public ObservableCollection<SchedulePeriod> Periods { get; } = [];
    public PeriodDraft? Draft => _draft;
    public bool HasDraft => _draft?.HasChanges == true;
    public bool IsPending => Fields.Any(preference => preference.IsPending) || _registering || _savingSchedule;
    public bool CanEdit => _owner.CanEdit;
    public bool CanRegister => CanEdit && !_registering;
    public bool CanSchedule => CanEdit && !_savingSchedule;
    public bool IsEditing => _draft is not null;
    public bool HasPeriods => Periods.Count > 0;
    public bool HasAlternative => Periods.Any(period => period.Mode == ScheduleMode.Alternative);
    public bool HasPaused => Periods.Any(period => period.Mode == ScheduleMode.Paused);
    public string PeriodSummary => Text.Format("preferences", "period_count", Periods.Count);
    public string EditorTitle => Text.Get("preferences", _editingIndex is null ? "new_period" : "edit_title");
    public string PreviewLabel => Preview?.TimeLabel ?? string.Empty;
    public string WeekStatus => Text.Get("preferences", Schedule.IsOn ? "schedule_active" : "schedule_inactive");
    public string AlternativeSummary => Text.Format("preferences", "period_group", FormatMode(ScheduleMode.Alternative), Periods.Count(period => period.Mode == ScheduleMode.Alternative));
    public string PausedSummary => Text.Format("preferences", "period_group", FormatMode(ScheduleMode.Paused), Periods.Count(period => period.Mode == ScheduleMode.Paused));
    public bool ShowsSelection => Selection is not null && !IsEditing;
    internal SchedulePeriod? Preview => _draft is { Start: not null, End: not null } draft && draft.Days.Any(day => day.IsChecked)
        ? new SchedulePeriod(this, draft) : null;
    public bool HasScheduleError => _scheduleError is not null || _invalidPeriod || _duplicatePeriod;
    public string ScheduleMessage => _duplicatePeriod ? Text.Get("preferences", "duplicate_period") :
        _invalidPeriod ? Text.Get("preferences", "invalid_period") :
        _scheduleError is null ? string.Empty : _owner.FormatError(_scheduleError);
    public bool HasRegistrationError => _registrationError is not null;
    public string RegistrationMessage => _registrationError is null ? string.Empty : _owner.FormatError(_registrationError);
    public bool Startup => _registration.ValueKind == JsonValueKind.Object && _registration.GetProperty("startup_enabled").GetBoolean();
    public bool HasRegistration => _registration.ValueKind == JsonValueKind.Object;
    public bool HandlersRegistered => HasRegistration && _registration.GetProperty("handlers_registered").GetBoolean();
    public string DefaultsMessage
    {
        get
        {
            var detail = Text.Get("preferences", "defaults_detail");
            if (!HandlersRegistered) return detail;
            var torrent = _registration.GetProperty("torrent_default");
            var magnet = _registration.GetProperty("magnet_default");
            if (torrent.ValueKind == JsonValueKind.True && magnet.ValueKind == JsonValueKind.False)
                return Text.Get("preferences", "magnet_default");
            if (magnet.ValueKind == JsonValueKind.True && torrent.ValueKind == JsonValueKind.False)
                return Text.Get("preferences", "torrent_default");
            return detail;
        }
    }
    public ICommand AddPeriod { get; }
    public ICommand SavePeriod { get; }
    public ICommand CancelPeriod { get; }
    public ICommand OpenDefaults { get; }
    public ICommand RemoveHandler { get; }
    public ICommand OpenStartup { get; }
    public string Language => Text.Language;
    public bool CanSelectLanguage => _owner.SwitchLanguage.CanExecute(null);
    public string OnText => Text.Get("preferences", "on");
    public string OffText => Text.Get("preferences", "off");
    public string Theme => _owner.Theme;
    public bool CanSelectTheme => _owner.SwitchTheme.CanExecute(null);
    public Task SelectTheme(string theme) => _owner.SelectTheme(theme);

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? TextChanged;
    public event EventHandler? WeekChanged;

    internal Preferences(MainViewModel owner, PipeClient client)
    {
        _owner = owner;
        _client = client;
        Destination = new(this, "default_destination");
        ShowAdd = new(this, "show_add");
        ShowSplash = new(this, "show_splash");
        StartInTray = new(this, "start_in_tray");
        Download = new(this, "download_limit");
        Upload = new(this, "upload_limit");
        AlternativeDownload = new(this, "alternative_download_limit");
        AlternativeUpload = new(this, "alternative_upload_limit");
        Downloads = new(this, "active_downloads");
        Seeds = new(this, "active_seeds");
        Connections = new(this, "connection_limit");
        Ratio = new(this, "ratio_limit");
        SeedingMinutes = new(this, "seeding_minutes");
        Interface = new(this, "network_interface");
        PortMapping = new(this, "port_mapping");
        Port = new(this, "listen_port");
        Notifications = new(this, "notifications_enabled");
        PreventSleep = new(this, "prevent_sleep");
        SeedingSleep = new(this, "prevent_sleep_seeding");
        Updates = new(this, "check_for_updates");
        Schedule = new(this, "schedule_enabled");
        Fields = [Destination, ShowAdd, Download, Upload, AlternativeDownload, AlternativeUpload,
            Downloads, Seeds, Connections, Ratio, SeedingMinutes, Interface, PortMapping, Port,
            Notifications, PreventSleep, SeedingSleep, Updates, Schedule, ShowSplash, StartInTray];
        AddPeriod = new Command(() => { Edit(null); return Task.CompletedTask; }, () => CanSchedule && !IsEditing);
        SavePeriod = new Command(CommitPeriod, () => CanSchedule && IsEditing);
        CancelPeriod = new Command(() => { CancelPeriodDraft(); return Task.CompletedTask; }, () => IsEditing && !_savingSchedule);
        OpenDefaults = new Command(() => Register("open_defaults"), () => CanRegister);
        RemoveHandler = new Command(() => Register("unregister_handlers"), () => CanRegister && HandlersRegistered);
        OpenStartup = new Command(() => Register("open_startup"), () => CanRegister);
    }

    internal void Apply(JsonElement settings)
    {
        var enabled = Schedule.IsOn;
        foreach (var field in Fields)
            if (settings.TryGetProperty(field.Name, out var value)) field.Confirm(value);
        if (settings.TryGetProperty("schedule", out var schedule)) ApplySchedule(schedule);
        if (enabled != Schedule.IsOn) WeekChanged?.Invoke(this, EventArgs.Empty);
        Refresh();
    }

    private void ApplySchedule(JsonElement schedule)
    {
        var values = schedule.EnumerateArray().Select(value => new SchedulePeriod(this, value)).ToArray();
        if (!_savingSchedule && _submittedSchedule is { } submitted && values.Length == submitted.Length &&
            values.Where((period, index) => !period.Matches(submitted[index])).Any() == false)
        {
            if (_draft is { } draft)
            {
                _editingIndex ??= submitted.Length - 1;
                if (draft.Start is not null && draft.End is not null && new SchedulePeriod(this, draft).Matches(submitted[_editingIndex.Value]))
                    CancelPeriodDraft();
            }
            _submittedSchedule = null;
        }
        if (Periods.Count == values.Length && !Periods.Where((period, index) => !period.Matches(values[index])).Any()) return;
        Selection = values.FirstOrDefault(period => Selection is { } selected && period.Matches(selected));
        Periods.Clear();
        foreach (var period in values) Periods.Add(period);
        WeekChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task ObserveRegistration() => Register("observe");
    public Task SetStartup(bool enabled) => Register(enabled ? "enable_startup" : "disable_startup");
    public void SelectLanguage(string language)
    {
        if (CanSelectLanguage) _owner.SelectLanguage(language);
    }

    private async Task Register(string operation)
    {
        if (!CanRegister) return;
        _registering = true;
        _registrationError = null;
        Refresh();
        try { _registration = await _client.Send("registration", new { operation }); }
        catch (Exception error)
        {
            _registrationError = error;
            if (error is CommandFailure && operation != "observe")
                try { _registration = await _client.Send("registration", new { operation = "observe" }); }
                catch (Exception) { }
        }
        finally { _registering = false; Refresh(); }
    }

    public async Task Commit(Preference field)
    {
        if (!field.CanEdit || !field.IsPending && !field.HasDraft) return;
        if (!TryValue(field, out var value)) { field.Invalid(); return; }
        await Submit(field, value);
    }

    public async Task<bool> PrepareLeave()
    {
        await Task.WhenAll(Fields.Select(field => field.Saving));
        var saved = true;
        foreach (var field in Fields)
        {
            if (!field.HasDraft) continue;
            if (!TryValue(field, out var value)) { field.Cancel(); continue; }
            if (field.HasFailure || !_owner.CanSave) { saved = false; continue; }
            await Submit(field, value);
            if (field.HasDraft) saved = false;
        }
        return saved && !Fields.Any(field => field.HasDraft);
    }

    public async Task Toggle(Preference field, bool value)
    {
        if (!field.CanEdit || field.IsOn == value) return;
        field.Choose(value);
        if (!field.HasDraft && !field.IsPending) { field.Cancel(); return; }
        await Submit(field, value);
    }

    private async Task Submit(Preference field, object value)
    {
        if (!_owner.CanSave) return;
        var submitted = field.Capture(value);
        if (field.IsPending)
        {
            field.Defer(submitted);
            await field.Saving;
            return;
        }
        field.Begin();
        Refresh();
        try
        {
            while (true)
            {
                try
                {
                    if (field.IsRate)
                    {
                        var saved = await _owner.SaveLimits(new Dictionary<string, double> { [field.Name] = (double)submitted.Value });
                        field.Accept(JsonSerializer.SerializeToElement(saved[field.Name]), submitted);
                    }
                    else
                    {
                        await _owner.SaveSettings(new Dictionary<string, object> { [field.Name] = submitted.Value });
                        field.Accept(JsonSerializer.SerializeToElement(submitted.Value), submitted);
                    }
                    if (field == Schedule) WeekChanged?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception error) { field.Reject(error); break; }
                if (!_owner.CanSave || field.TakeIntent() is not { } next) break;
                submitted = next;
            }
        }
        finally { field.End(); Refresh(); }
    }

    private static bool TryValue(Preference field, out object value)
    {
        value = field.Input.Trim();
        if (field.Name is "default_destination" or "network_interface") return true;
        if (field.IsBoolean) { value = field.IsOn; return true; }
        if (!double.TryParse(field.Input, NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.CurrentCulture, out var number)) return false;
        if (field.IsRate) { value = number; return MainViewModel.IsValidLimit(number); }
        if (!double.IsFinite(number) || number < 0) return false;
        if (field.Name == "ratio_limit") { value = number; return true; }
        if (number != Math.Truncate(number) || number > int.MaxValue ||
            field.Name == "listen_port" && (number < 1 || number > 65535)) return false;
        value = (int)number;
        return true;
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
        draft.Start = TimeSpan.FromMinutes(span.Start);
        draft.End = TimeSpan.FromMinutes(span.End);
    }

    internal async Task Reschedule(SchedulePeriod period, PeriodSpan span)
    {
        if (!CanSchedule || IsEditing || !Periods.Contains(period) || period.Span == span) return;
        var changed = period.WithSpan(span);
        if (await SubmitPeriod(changed, Periods.IndexOf(period))) return;
        var current = Periods.FirstOrDefault(candidate => candidate.Matches(period) || candidate.Matches(changed));
        if (current is null) return;
        BeginEdit(current);
        if (_draft is { } draft)
        {
            draft.Start = TimeSpan.FromMinutes(span.Start);
            draft.End = TimeSpan.FromMinutes(span.End);
        }
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
        CancelPeriodDraft();
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
            await _owner.SaveSettings(new { schedule });
            ApplySchedule(JsonSerializer.SerializeToElement(schedule));
            _submittedSchedule = null;
            return true;
        }
        catch (Exception error) { _scheduleError = error; return false; }
        finally { _savingSchedule = false; Refresh(); }
    }

    private void CancelPeriodDraft()
    {
        _draft = null;
        _editingIndex = null;
        _submittedSchedule = null;
        _scheduleError = null;
        _invalidPeriod = false;
        _duplicatePeriod = false;
        Refresh();
    }

    public void CancelDraft()
    {
        foreach (var field in Fields) field.Cancel();
        CancelPeriodDraft();
    }

    public void RefreshText()
    {
        Refresh();
        _draft?.Refresh();
        TextChanged?.Invoke(this, EventArgs.Empty);
        WeekChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        foreach (var field in Fields) field.Refresh();
        foreach (var period in Periods) period.Refresh();
        foreach (Command command in new[] { AddPeriod, SavePeriod, CancelPeriod, OpenDefaults, RemoveHandler, OpenStartup }) command.Refresh();
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

public sealed class Preference(Preferences owner, string name) : INotifyPropertyChanged
{
    private string _input = string.Empty;
    private string _confirmedInput = string.Empty;
    private JsonElement _confirmed;
    private Exception? _failure;
    private bool _invalid;
    private bool? _choice;
    private TaskCompletionSource? _saving;
    private Submission? _intent;
    private bool _cancelled;
    public string Name { get; } = name;
    public bool IsRate => Name is "download_limit" or "upload_limit" or "alternative_download_limit" or "alternative_upload_limit";
    public string Label => owner.Text.Get(IsRate ? "limits" : "preferences", Name);
    public string Input
    {
        get => _input;
        set { if (_input == value) return; _input = value; _cancelled = false; _failure = null; _invalid = false; Refresh(); owner.Refresh(); }
    }
    public bool IsOn => _choice ?? _confirmed.ValueKind == JsonValueKind.True;
    public bool HasDraft => _input != _confirmedInput || _choice is { } choice && choice != (_confirmed.ValueKind == JsonValueKind.True);
    public bool IsPending => _saving is not null;
    internal bool IsBoolean => _confirmed.ValueKind is JsonValueKind.True or JsonValueKind.False;
    internal bool HasFailure => _failure is not null;
    internal Task Saving => _saving?.Task ?? Task.CompletedTask;
    // A save keeps its control enabled so later input keeps focus.
    public bool CanEdit => owner.CanEdit;
    public string Message => _invalid ? owner.Text.Get(IsRate ? "errors" : "preferences", IsRate ? "invalid_limits" : Name == "listen_port" ? "invalid_port" : "invalid_number") :
        _failure is null ? string.Empty :
        _failure is CommandFailure ? _failure.Message : owner.Text.Error("unknown", _failure.Message);
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Confirm(JsonElement value)
    {
        var preserve = HasDraft || IsPending;
        _confirmed = value.Clone();
        _confirmedInput = value.ValueKind == JsonValueKind.String ? value.GetString()! :
            value.ValueKind == JsonValueKind.Number ? (value.GetDouble() / (IsRate ? 1024 : 1)).ToString("G", CultureInfo.CurrentCulture) : string.Empty;
        if (!preserve) { _input = _confirmedInput; _choice = null; }
        Refresh();
    }
    internal sealed record Submission(object Value, string Input, bool? Choice);
    internal Submission Capture(object value) { _cancelled = false; return new(value, _input, _choice); }
    internal void Defer(Submission submitted) => _intent = submitted;
    internal Submission? TakeIntent()
    {
        var intent = _intent;
        _intent = null;
        return intent;
    }
    internal void Accept(JsonElement value, Submission submitted)
    {
        var preserve = !_cancelled && (_input != submitted.Input || _choice != submitted.Choice);
        Confirm(value);
        if (!preserve) { _input = _confirmedInput; _choice = null; _invalid = false; }
        _cancelled = false;
        _failure = null;
        Refresh();
    }
    internal void Begin() { _saving = new(TaskCreationOptions.RunContinuationsAsynchronously); _failure = null; _invalid = false; Refresh(); }
    internal void End()
    {
        var saving = _saving;
        _saving = null;
        _intent = null;
        _cancelled = false;
        Refresh();
        saving?.TrySetResult();
    }
    internal void Reject(Exception error) { _failure = error; Refresh(); }
    internal void Invalid() { _invalid = true; Refresh(); }
    internal void Choose(bool value) { _choice = value; _cancelled = false; _failure = null; _invalid = false; Refresh(); }
    public void Cancel()
    {
        _intent = null;
        _cancelled = IsPending;
        _input = _confirmedInput;
        _choice = null;
        _failure = null;
        _invalid = false;
        Refresh();
        owner.Refresh();
    }
    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

public sealed class SchedulePeriod : INotifyPropertyChanged
{
    private readonly Preferences _owner;
    public IReadOnlyList<int> Days { get; } = [];
    public int Start { get; }
    public int End { get; }
    public ScheduleMode Mode { get; }
    internal PeriodSpan Span => new(Start, End > Start ? End - Start : 1440 - Start + End);
    public string Summary => _owner.Text.Format("preferences", "period_summary", _owner.DescribeDays(Days), TimeLabel);
    public string TimeRange => Start == 0 && End == 0 ? _owner.Text.Get("preferences", "time_all_day") :
        _owner.Text.Format("preferences", End <= Start ? "time_overnight" : "time_range",
            Preferences.Time(Start), Preferences.Time(End));
    public string TimeLabel => _owner.Text.Format("preferences", "time_summary", TimeRange,
        Span.Duration < 60 ? _owner.Text.Format("preferences", "duration_minutes", Span.Duration) :
        Span.Duration % 60 == 0 ? _owner.Text.Format("preferences", "duration_hours", Span.Duration / 60) :
        _owner.Text.Format("preferences", "duration_both", Span.Duration / 60, Span.Duration % 60));
    public string ModeLabel => _owner.FormatMode(Mode);
    public string Description => Start == 0 && End == 0
        ? _owner.Text.Format("preferences", "period_all_day", _owner.DescribeDays(Days), _owner.FormatMode(Mode))
        : _owner.Text.Format("preferences", End <= Start ? "period_overnight" : "period", _owner.DescribeDays(Days),
            Preferences.Time(Start), Preferences.Time(End), _owner.FormatMode(Mode));
    public string EditText => _owner.Text.Get("preferences", "edit");
    public string RemoveText => _owner.Text.Get("preferences", "remove");
    public string EditName => _owner.Text.Format("preferences", "edit_period", Description);
    public string RemoveName => _owner.Text.Format("preferences", "remove_period", Description);
    public ICommand Edit { get; }
    public ICommand Remove { get; }
    public event PropertyChangedEventHandler? PropertyChanged;

    private SchedulePeriod(Preferences owner)
    {
        _owner = owner;
        Edit = new Command(() => { owner.Edit(this); return Task.CompletedTask; }, () => owner.CanSchedule && !owner.IsEditing);
        Remove = new Command(() => owner.Remove(this), () => owner.CanSchedule && !owner.IsEditing);
    }
    internal SchedulePeriod(Preferences owner, JsonElement value) : this(owner)
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
    internal SchedulePeriod(Preferences owner, PeriodDraft draft) : this(owner)
    {
        Days = draft.Days.Where(day => day.IsChecked).Select(day => day.Index).ToArray();
        Start = (int)draft.Start.GetValueOrDefault().TotalMinutes;
        End = (int)draft.End.GetValueOrDefault().TotalMinutes;
        Mode = draft.IsPaused ? ScheduleMode.Paused : ScheduleMode.Alternative;
    }
    internal SchedulePeriod(Preferences owner, IReadOnlyList<int> days, PeriodSpan span, ScheduleMode mode) : this(owner)
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
    private readonly Preferences _owner;
    private readonly SchedulePeriod? _original;
    private TimeSpan? _start;
    private TimeSpan? _end;
    private bool _paused;
    public DayChoice[] Days { get; }
    public TimeSpan? Start
    {
        get => _start;
        set { if (_start == value) return; _start = value; Refresh(); _owner.Refresh(); }
    }
    public TimeSpan? End
    {
        get => _end;
        set { if (_end == value) return; _end = value; Refresh(); _owner.Refresh(); }
    }
    public bool IsPaused
    {
        get => _paused;
        set { if (_paused == value) return; _paused = value; Refresh(); _owner.Refresh(); }
    }
    public bool HasChanges => _original is null || Start?.TotalMinutes != _original.Start || End?.TotalMinutes != _original.End ||
        IsPaused != (_original.Mode == ScheduleMode.Paused) || !Days.Where(day => day.IsChecked).Select(day => day.Index).SequenceEqual(_original.Days);
    public event PropertyChangedEventHandler? PropertyChanged;
    internal PeriodDraft(Preferences owner, SchedulePeriod? period)
    {
        _owner = owner;
        _original = period;
        Days = Enumerable.Range(0, 7).Select(index => new DayChoice(owner, index,
            period is null || period.Days.Contains(index))).ToArray();
        _start = TimeSpan.FromMinutes(period?.Start ?? 9 * 60);
        _end = TimeSpan.FromMinutes(period?.End ?? 17 * 60);
        _paused = period?.Mode == ScheduleMode.Paused;
    }
    internal void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        foreach (var day in Days) day.Refresh();
    }
}

public sealed class DayChoice(Preferences owner, int index, bool isChecked) : INotifyPropertyChanged
{
    private bool _checked = isChecked;
    public int Index { get; } = index;
    public string Label => owner.Day(Index);
    public string AutomationId => "PeriodDay" + Index;
    public bool IsChecked
    {
        get => _checked;
        set { if (_checked == value) return; _checked = value; Refresh(); owner.Refresh(); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

internal sealed record ScheduleRange(int Start, int End, ScheduleMode Mode, SchedulePeriod? Period);
