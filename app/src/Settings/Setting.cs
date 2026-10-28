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
    private JsonElement? _confirmed;
    private bool? _choice;
    private SaveState _save = new SaveState.Idle();
    private Problem _problem = new Problem.None();
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
    // Each confirmed value is null while no value is confirmed, so a summary
    // shows that it is unknown instead of a default.
    internal string? ConfirmedText => _confirmed is null ? null : _confirmedInput;
    internal double? ConfirmedNumber =>
        _confirmed is { ValueKind: JsonValueKind.Number } number ? number.GetDouble() : null;
    internal bool? ConfirmedOn =>
        _confirmed is { } value ? value.ValueKind == JsonValueKind.True : null;
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
            ClearCancel();
            _problem = new Problem.None();
            Change();
        }
    }
    public bool IsOn => _choice ?? ConfirmedOn == true;

    // The tag of the item a selector shows, as Select reads it.
    public string SelectedValue =>
        Kind == SettingKind.Boolean ? IsOn ? "true" : "false"
        : Input;
    public bool HasDraft =>
        _input != _confirmedInput
        || _choice is { } choice && choice != (ConfirmedOn == true)
        || _save is SaveState.Unconfirmed { Cancelled: false } lost
            && !lost.Sent.IsCurrent(_input, _choice);
    public bool IsPending => _save is SaveState.Running;
    internal Exception? Failure => _problem is Problem.Failed failed ? failed.Error : null;
    internal Task Saving =>
        _save is SaveState.Running running ? running.Done.Task : Task.CompletedTask;

    private bool Cancelled =>
        _save is SaveState.Running { Cancelled: true } or SaveState.Unconfirmed { Cancelled: true };

    // A save keeps its control enabled so later input keeps focus.
    public bool CanEdit => Store.CanEdit() && (Condition?.Invoke() ?? true);
    public string Message
    {
        get
        {
            if (_problem is not Problem.Invalid)
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
            _confirmed is not { } confirmed
            || confirmed.ValueKind != value.ValueKind
            || !JsonElement.DeepEquals(confirmed, value);
        if (changed)
        {
            var preserve = HasDraft || IsPending || _save is SaveState.Unconfirmed;
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
            _save is SaveState.Unconfirmed lost
            && JsonElement.DeepEquals(value, JsonSerializer.SerializeToElement(lost.Sent.Value))
        )
        {
            Settle(lost.Sent);
            changed = true;
        }
        if (changed)
            Refresh();
        return changed;
    }

    private sealed record Submission(object Value, string Input, bool? Choice)
    {
        // Whether the field still shows this submission, so its outcome
        // belongs to the input the person sees.
        internal bool IsCurrent(string input, bool? choice) => Input == input && Choice == choice;
    }

    // Where the last submitted value stands. Cancelled records a Cancel made
    // while the save ran or waited for confirmation, so the outcome discards
    // the input instead of keeping it.
    private abstract record SaveState
    {
        private SaveState() { }

        public sealed record Idle : SaveState;

        // Next is the newest value submitted while this save runs.
        public sealed record Running(TaskCompletionSource Done, Submission? Next, bool Cancelled)
            : SaveState;

        // The reply to Sent was lost, so a snapshot that reports Sent settles it.
        public sealed record Unconfirmed(Submission Sent, bool Cancelled) : SaveState;
    }

    // What the field reports under it: input that cannot be saved, or a
    // failed save of the input it shows.
    private abstract record Problem
    {
        private Problem() { }

        public sealed record None : Problem;

        public sealed record Invalid : Problem;

        public sealed record Failed(Exception Error) : Problem;
    }

    public void SelectUnit(string unit)
    {
        if (DurationUnit == unit)
            return;
        var scale = DurationScale;
        DurationUnit = unit;
        if (Editor.TryNumber(_input, out var number))
            _input = (number * scale / DurationScale).ToString("G", CultureInfo.CurrentCulture);
        _confirmedInput =
            ConfirmedNumber is not { } minutes || minutes == 0
                ? string.Empty
                : (minutes / DurationScale).ToString("G", CultureInfo.CurrentCulture);
        Change();
    }

    /// <summary>Saves valid typed input; invalid input shows its message instead.</summary>
    public async Task Commit()
    {
        if (!CanEdit || !IsPending && !HasDraft)
            return;
        if (!TryValue(out var value))
        {
            _problem = new Problem.Invalid();
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
        if (Cancelled || !IsPending && !HasDraft)
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
        if (_problem is not Problem.Failed)
            await Submit(value);
    }

    public async Task Toggle(bool value)
    {
        if (!CanEdit || IsOn == value)
            return;
        _choice = value;
        ClearCancel();
        _problem = new Problem.None();
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
        _save = _save switch
        {
            SaveState.Running running => running with { Next = null, Cancelled = true },
            SaveState.Unconfirmed lost => lost with { Cancelled = true },
            _ => _save,
        };
        _input = _confirmedInput;
        _choice = null;
        _problem = new Problem.None();
        Change();
    }

    // Input typed or chosen after Cancel is the person's again, so the save
    // keeps it when it settles.
    private void ClearCancel() =>
        _save = _save switch
        {
            SaveState.Running running => running with { Cancelled = false },
            SaveState.Unconfirmed lost => lost with { Cancelled = false },
            _ => _save,
        };

    /// <summary>
    /// Saves the value without the <see cref="CanEdit"/> check of the
    /// setting's own control. A store that cannot save rejects the value and
    /// raises <see cref="Failed"/>.
    /// </summary>
    internal async Task Submit(object value)
    {
        if (!Store.CanSave())
        {
            var error = new IOException(_text.Get("connection", "unavailable"));
            Reject(error, null);
            Failed?.Invoke(this, error);
            return;
        }
        var submitted = new Submission(value, _input, _choice);
        if (_save is SaveState.Running running)
        {
            _save = running with { Next = submitted, Cancelled = false };
            await running.Done.Task;
            return;
        }
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _save = new SaveState.Running(done, null, false);
        _problem = new Problem.None();
        Change();
        Submission? lost = null;
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
                    && TakeNext() is { } next
                    && next != submitted
                )
                {
                    submitted = next;
                    continue;
                }
                if (failure is not null)
                    lost = Reject(failure, submitted);
                break;
            }
        }
        finally
        {
            // A lost reply still needs confirmation, even after the input
            // changes or is cancelled; Settle preserves any newer input when
            // it arrives.
            _save = lost is null ? new SaveState.Idle() : new SaveState.Unconfirmed(lost, Cancelled);
            Change();
            done.TrySetResult();
        }
    }

    private Submission? TakeNext()
    {
        if (_save is not SaveState.Running { Next: { } next } running)
            return null;
        _save = running with { Next = null };
        return next;
    }

    // Ends the save of submitted with its value confirmed or refused, and
    // keeps newer input unless Cancel discarded it.
    private void Settle(Submission submitted)
    {
        var preserve = !Cancelled && !submitted.IsCurrent(_input, _choice);
        if (!preserve)
        {
            _input = _confirmedInput;
            _choice = null;
        }
        _problem = preserve && _problem is Problem.Invalid ? _problem : new Problem.None();
        _save = _save is SaveState.Running running
            ? running with { Cancelled = false }
            : new SaveState.Idle();
    }

#if CAPTURE
    internal void Reject(Exception error) => Reject(error, null);
#endif

    // Reports a failed save of submitted, or of nothing when the store could
    // not save. Returns the submission whose reply was lost, which a snapshot
    // must confirm; a refusal proves the value unsaved and returns null.
    private Submission? Reject(Exception error, Submission? submitted)
    {
        var cancelled = Cancelled;
        var current = submitted is null || !cancelled && submitted.IsCurrent(_input, _choice);
        var refused = Store.Refused(error);
        if (cancelled && submitted is not null && refused)
            Settle(submitted);
        _problem =
            current ? new Problem.Failed(error)
            : _problem is Problem.Invalid ? _problem
            : new Problem.None();
        if (submitted is null && _save is SaveState.Unconfirmed)
            _save = new SaveState.Idle();
        Refresh();
        return refused ? null : submitted;
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
