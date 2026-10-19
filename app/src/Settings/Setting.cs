using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Syno.TinyTorrent.Shared;

namespace Syno.TinyTorrent.Settings;

public sealed class Setting : INotifyPropertyChanged
{
    private readonly Strings _text;
    private string _input = string.Empty;
    private string _confirmedInput = string.Empty;
    private JsonElement _confirmed;
    private Exception? _failure;
    private bool _invalid;
    private bool? _choice;
    private TaskCompletionSource? _saving;
    private Submission? _intent;
    private Submission? _uncertain;
    private bool _cancelled;
    private bool? _unlimited;

    internal Setting(
        SettingStore store,
        Strings text,
        string name,
        SettingKind kind,
        SettingsCategory category
    )
    {
        Store = store;
        _text = text;
        Name = name;
        Kind = kind;
        Category = category;
        Section = kind == SettingKind.Rate ? "limits" : "settings";
    }

    public string Name { get; }
    public SettingKind Kind { get; }
    public SettingsCategory Category { get; }
    public int Minimum { get; init; }
    public int Maximum { get; init; } = int.MaxValue;
    public string Section { get; init; }
    public string? ValidationKey { get; init; }
    public bool IsDuration { get; init; }

    public string DurationUnit { get; private set; } = "minutes";
    internal int DurationScale => IsDuration && DurationUnit == "hours" ? 60 : 1;

    // Other state that must hold before the setting can be edited.
    internal Func<bool>? Condition { get; init; }
    private SettingStore Store { get; }
    public bool IsRate => Kind == SettingKind.Rate;
    internal RateScale RateScale { get; init; } = RateScale.MbPerSecond;
    public string RateUnit => _text.RateUnit(RateScale);

    // Empty limits save 0, which the engine reads as unlimited. Numbers whose
    // range starts at 0 have it unless the registration says otherwise.
    public bool HasUnlimited
    {
        get =>
            _unlimited
            ?? Kind is SettingKind.Rate or SettingKind.Integer or SettingKind.Number
                && Minimum == 0;
        init => _unlimited = value;
    }
    internal string ConfirmedText => _confirmedInput;
    internal double ConfirmedNumber =>
        _confirmed.ValueKind == JsonValueKind.Number ? _confirmed.GetDouble() : 0;
    internal bool ConfirmedOn => _confirmed.ValueKind == JsonValueKind.True;
    public string Label => _text.Get(Section, Name);

    // Names the duration's unit selector after its setting, so each one reads
    // differently in Narrator.
    public string UnitLabel => _text.Format("settings", "duration_unit", Label);
    public string Input
    {
        get => _input;
        set
        {
            if (_input == value)
                return;
            _input = value;
            _cancelled = false;
            _failure = null;
            _invalid = false;
            Change();
        }
    }
    public bool IsOn => _choice ?? _confirmed.ValueKind == JsonValueKind.True;

    // The tag of the item a selector shows, as Select reads it.
    public string SelectedValue =>
        Kind == SettingKind.Boolean ? IsOn ? "true" : "false"
        : Input;
    public bool HasDraft =>
        _input != _confirmedInput
        || _choice is { } choice && choice != (_confirmed.ValueKind == JsonValueKind.True)
        || !_cancelled
            && _uncertain is { } submitted
            && (_input != submitted.Input || _choice != submitted.Choice);
    public bool IsPending => _saving is not null;
    internal Exception? Failure => _failure;
    internal Task Saving => _saving?.Task ?? Task.CompletedTask;

    // A save keeps its control enabled so later input keeps focus.
    public bool CanEdit => Store.CanEdit() && (Condition?.Invoke() ?? true);
    public string Message
    {
        get
        {
            if (!_invalid)
                return string.Empty;
            if (ValidationKey is { } key)
                return _text.Get(Section, key);
            if (IsDuration)
                return _text.Get("settings", "invalid_duration");
            if (IsRate)
                return _text.Format(
                    "settings",
                    "invalid_rate",
                    Maximum / (double)RateScale,
                    RateUnit
                );
            if (Minimum > 0)
                return _text.Format("settings", "invalid_range", Minimum, Maximum);
            return _text.Get(
                "settings",
                Kind == SettingKind.Integer ? "invalid_count" : "invalid_number"
            );
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raised when the input, the choice or the save state changes, so values
    /// derived from several settings can follow. A refresh of text or
    /// availability raises only <see cref="PropertyChanged"/>.
    /// </summary>
    internal event EventHandler? Changed;

    /// <summary>Raised for each failed save, for the owner to report.</summary>
    internal event EventHandler<Exception>? Failed;

    internal bool Confirm(JsonElement value)
    {
        var changed =
            _confirmed.ValueKind != value.ValueKind || !JsonElement.DeepEquals(_confirmed, value);
        if (changed)
        {
            var preserve = HasDraft || IsPending || _uncertain is not null;
            // Cloning a child of the snapshot's clone retains the whole snapshot.
            _confirmed = JsonSerializer.SerializeToElement(value);
            _confirmedInput =
                value.ValueKind == JsonValueKind.String ? value.GetString()!
                : value.ValueKind != JsonValueKind.Number || HasUnlimited && value.GetDouble() == 0
                    ? string.Empty
                : IsRate ? Editor.RateInput(value.GetInt32(), RateScale)
                : (value.GetDouble() / DurationScale).ToString("G", CultureInfo.CurrentCulture);
            if (!preserve)
            {
                _input = _confirmedInput;
                _choice = null;
            }
        }
        if (
            !IsPending
            && _uncertain is { } submitted
            && JsonElement.DeepEquals(value, JsonSerializer.SerializeToElement(submitted.Value))
        )
        {
            Settle(submitted);
            changed = true;
        }
        if (changed)
            Refresh();
        return changed;
    }

    private sealed record Submission(object Value, string Input, bool? Choice);

    public void SelectUnit(string unit)
    {
        if (DurationUnit == unit)
            return;
        var scale = DurationScale;
        DurationUnit = unit;
        if (Editor.TryNumber(_input, out var number))
            _input = (number * scale / DurationScale).ToString("G", CultureInfo.CurrentCulture);
        _confirmedInput =
            ConfirmedNumber == 0
                ? string.Empty
                : (ConfirmedNumber / DurationScale).ToString("G", CultureInfo.CurrentCulture);
        Change();
    }

    /// <summary>Saves valid typed input; invalid input shows its message instead.</summary>
    public async Task Commit()
    {
        if (!CanEdit || !IsPending && !HasDraft)
            return;
        if (!TryValue(out var value))
        {
            _invalid = true;
            Refresh();
            return;
        }
        await Submit(value);
    }

    /// <summary>
    /// Saves valid typed input when the person leaves the field or Settings,
    /// and restores the saved value of input that cannot be saved.
    /// </summary>
    public async Task Depart()
    {
        if (_cancelled || !IsPending && !HasDraft)
        {
            await Saving;
            return;
        }
        // A draft that its condition has disabled can be neither corrected nor
        // saved, so leaving discards it like invalid input.
        if (!TryValue(out var value) || Condition?.Invoke() == false)
        {
            Cancel();
            return;
        }
        if (!Store.CanSave())
            return;
        if (_failure is null)
            await Submit(value);
    }

    public async Task Toggle(bool value)
    {
        if (!CanEdit || IsOn == value)
            return;
        _choice = value;
        _cancelled = false;
        _failure = null;
        _invalid = false;
        Change();
        if (!HasDraft && !IsPending)
        {
            Cancel();
            return;
        }
        await Submit(value);
    }

    /// <summary>
    /// Saves the value a choice control selected, which is its item's tag:
    /// "true" or "false" for an on/off setting, otherwise the input. The
    /// current value saves nothing.
    /// </summary>
    public async Task Select(string value)
    {
        if (value == SelectedValue)
            return;
        if (Kind == SettingKind.Boolean)
        {
            await Toggle(value == "true");
            return;
        }
        Input = value;
        await Commit();
    }

    public void Cancel()
    {
        _intent = null;
        _cancelled = IsPending || _uncertain is not null;
        _input = _confirmedInput;
        _choice = null;
        _failure = null;
        _invalid = false;
        Change();
    }

    private async Task Submit(object value)
    {
        if (!Store.CanSave())
        {
            var error = new IOException(_text.Get("connection", "unavailable"));
            Reject(error, null);
            Failed?.Invoke(this, error);
            return;
        }
        _cancelled = false;
        var submitted = new Submission(value, _input, _choice);
        if (IsPending)
        {
            _intent = submitted;
            await Saving;
            return;
        }
        _saving = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _failure = null;
        _uncertain = null;
        _invalid = false;
        Change();
        try
        {
            while (true)
            {
                Exception? failure = null;
                try
                {
                    await Store.Save(this, submitted.Value);
                    Confirm(JsonSerializer.SerializeToElement(submitted.Value));
                    Settle(submitted);
                    Refresh();
                }
                catch (Exception error)
                {
                    failure = error;
                    Failed?.Invoke(this, error);
                }
                if (
                    Store.CanSave()
                    && (failure is null || Store.Refused(failure))
                    && TakeIntent() is { } next
                    && next != submitted
                )
                {
                    submitted = next;
                    continue;
                }
                if (failure is not null)
                    Reject(failure, submitted);
                break;
            }
        }
        finally
        {
            var saving = _saving;
            _saving = null;
            _intent = null;
            if (_uncertain is null)
                _cancelled = false;
            Change();
            saving?.TrySetResult();
        }
    }

    private Submission? TakeIntent()
    {
        var intent = _intent;
        _intent = null;
        return intent;
    }

    private void Settle(Submission submitted)
    {
        var preserve = !_cancelled && (_input != submitted.Input || _choice != submitted.Choice);
        if (!preserve)
        {
            _input = _confirmedInput;
            _choice = null;
            _invalid = false;
        }
        _cancelled = false;
        _failure = null;
        _uncertain = null;
    }

#if CAPTURE
    internal void Reject(Exception error) => Reject(error, null);
#endif

    private void Reject(Exception error, Submission? submitted)
    {
        var current =
            submitted is null
            || !_cancelled && _input == submitted.Input && _choice == submitted.Choice;
        var refused = Store.Refused(error);
        if (_cancelled && submitted is not null && refused)
            Settle(submitted);
        _failure = current ? error : null;
        // A lost reply still needs confirmation, even after the input changes
        // or is cancelled; Settle preserves any newer input when it arrives.
        _uncertain = refused ? null : submitted;
        Refresh();
    }

    private bool TryValue(out object value)
    {
        value = Input.Trim();
        if (Kind == SettingKind.Text)
            return true;
        if (Kind == SettingKind.Boolean)
        {
            value = IsOn;
            return true;
        }
        if (HasUnlimited && Input.Trim().Length == 0)
        {
            value = Kind == SettingKind.Number ? 0.0 : (object)0;
            return true;
        }
        if (IsRate)
        {
            if (!Editor.TryRate(Input, out var bytes, RateScale) || bytes > Maximum)
                return false;
            value = bytes;
            return true;
        }
        double number;
        if (Kind == SettingKind.Integer && !IsDuration)
        {
            if (!Editor.TryInteger(Input, out var integer))
                return false;
            number = integer;
        }
        else if (!Editor.TryNumber(Input, out number))
            return false;
        if (IsDuration)
            number = Math.Round(number * DurationScale, 8);
        if (Kind == SettingKind.Number)
        {
            value = number;
            return true;
        }
        if (
            number != Math.Truncate(number)
            || number > int.MaxValue
            || number < Minimum && !(HasUnlimited && number == 0)
            || number > Maximum
        )
            return false;
        value = (int)number;
        return true;
    }

    private void Change()
    {
        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void Refresh() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

/// <summary>
/// Where a <see cref="Setting"/> saves: whether it can be edited and saved
/// now, the save of a submitted value, and whether a failed save proves the
/// value is unsaved. Otherwise the setting waits for a confirmed value.
/// </summary>
internal sealed record SettingStore(
    Func<bool> CanEdit,
    Func<bool> CanSave,
    Func<Setting, object, Task> Save,
    Func<Exception, bool> Refused
);
