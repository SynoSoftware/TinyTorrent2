using System.ComponentModel;
using System.Globalization;
using System.Net.NetworkInformation;
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
    private (string Operation, Exception Error)? _registrationError;
    private bool _registering;
    private InterfaceChoice? _unavailableInterface;
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
    public Preference Encryption { get; }
    public Proxy Proxy { get; }
    public Preference Ratio { get; }
    public Preference SeedingMinutes { get; }
    public Preference Interface { get; }
    public IReadOnlyList<InterfaceChoice> Interfaces { get; private set; } = [];
    public InterfaceChoice? SelectedInterface => Interfaces.FirstOrDefault(choice => choice.InterfaceId == Interface.Input);
    public Preference PortMapping { get; }
    public Preference Port { get; }
    // Peers cannot connect in through a proxy, so the port and its forwarding
    // have no effect while one is in use.
    public bool CanEditPort => Port.CanEdit && !Proxy.IsInUse;
    public bool CanEditMapping => PortMapping.CanEdit && !Proxy.IsInUse;
    public string PortHint => Text.Get("preferences", Proxy.IsInUse ? "proxy_port_hint" : "port_hint");
    public string MappingHint => Text.Get("preferences", Proxy.IsInUse ? "proxy_port_hint" : "mapping_hint");
    public Preference ProblemNotifications { get; }
    public Preference FinishedNotifications { get; }
    public Preference AddedNotifications { get; }
    public Preference PreventSleep { get; }
    public Preference SeedingSleep { get; }
    // Seeding only extends Prevent sleep, so it has no effect while that is off.
    public bool CanEditSeedingSleep => SeedingSleep.CanEdit && PreventSleep.IsOn;
    public Preference Updates { get; }
    public Schedule Schedule { get; }
    public Preference Language { get; }
    public Preference Theme { get; }
    public IReadOnlyList<Preference> Fields { get; }
    internal Preference? RefusedField => Fields.FirstOrDefault(preference => preference.HasDraft && preference.Failure is CommandFailure);
    public bool HasError => RefusedField is not null || Schedule.HasDraft && Schedule.HasScheduleError;
    public bool IsPending => Fields.Any(preference => preference.IsPending) || _registering || Schedule.IsPending;
    public bool CanEdit => _owner.CanEdit;
    internal bool CanSave => _owner.CanSave;
    public bool CanRegister => CanEdit && !_registering;
    private bool HasStartupError => _registrationError?.Operation is "enable_startup" or "disable_startup" or "open_startup";
    private string RegistrationMessage => _registrationError is { } failure ? Text.Error(failure.Error) : string.Empty;
    public string StartupMessage => HasStartupError ? RegistrationMessage : string.Empty;
    public string HandlersMessage => !HasStartupError ? RegistrationMessage : string.Empty;
    public bool HasRegistration => _registration.ValueKind == JsonValueKind.Object;
    // Another TinyTorrent copy's entry is still TinyTorrent's registration, so
    // it counts as on and the Other message names that copy.
    public bool Startup => Registered("startup") != "none";
    public string StartupOtherMessage => Other("startup");
    public bool HandlersRegistered => Registered("handlers") != "none";
    public string HandlersOtherMessage => Other("handlers");
    // Windows lets only the person choose the default app, so a registration
    // that is not the default yet offers Windows Default apps.
    public bool NeedsDefaults => HandlersRegistered &&
        (Field("torrent_default").ValueKind == JsonValueKind.False || Field("magnet_default").ValueKind == JsonValueKind.False);
    public string DefaultsMessage
    {
        get
        {
            var detail = Text.Get("preferences", "defaults_detail");
            if (!HandlersRegistered) return detail;
            var torrent = Field("torrent_default");
            var magnet = Field("magnet_default");
            if (torrent.ValueKind == JsonValueKind.True && magnet.ValueKind == JsonValueKind.False)
                return Text.Get("preferences", "magnet_default");
            if (magnet.ValueKind == JsonValueKind.True && torrent.ValueKind == JsonValueKind.False)
                return Text.Get("preferences", "torrent_default");
            return detail;
        }
    }
    public ICommand OpenDefaults { get; }
    public ICommand OpenStartup { get; }

    // "this", "other" or "none": whether TinyTorrent's entry starts this copy,
    // another TinyTorrent copy, or nothing.
    private string Registered(string name) => Field(name) is { ValueKind: JsonValueKind.String } state ? state.GetString() ?? "none" : "none";
    private string Other(string name) => Registered(name) == "other" && Field(name + "_target") is { ValueKind: JsonValueKind.String } target
        ? Text.Format("preferences", name + "_other", target.GetString() ?? string.Empty)
        : string.Empty;
    // An engine from another build can omit a field; a missing field reads as
    // Undefined, so the page shows the setting as off instead of failing.
    private JsonElement Field(string name) =>
        HasRegistration && _registration.TryGetProperty(name, out var value) ? value : default;
    public bool CanSelectLanguage => CanEdit;
    public string OnText => Text.Get("preferences", "on");
    public string OffText => Text.Get("preferences", "off");
    public bool CanSelectTheme => CanEdit;
    public async Task SelectTheme(string theme)
    {
        if (!CanSelectTheme) return;
        Theme.Input = theme;
        if (!Theme.HasDraft && !Theme.IsPending) { Theme.Cancel(); return; }
        await Commit(Theme);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? TextChanged;

    internal Preferences(MainViewModel owner, PipeClient client)
    {
        _owner = owner;
        _client = client;
        Destination = new(this, "default_destination", PreferenceKind.Text, PreferenceSection.General);
        ShowAdd = new(this, "show_add", PreferenceKind.Boolean, PreferenceSection.General);
        ShowSplash = new(this, "show_splash", PreferenceKind.Boolean, PreferenceSection.General);
        StartInTray = new(this, "start_in_tray", PreferenceKind.Boolean, PreferenceSection.General);
        Download = new(this, "download_limit", PreferenceKind.Rate, PreferenceSection.Limits);
        Upload = new(this, "upload_limit", PreferenceKind.Rate, PreferenceSection.Limits);
        AlternativeDownload = new(this, "alternative_download_limit", PreferenceKind.Rate, PreferenceSection.Limits);
        AlternativeUpload = new(this, "alternative_upload_limit", PreferenceKind.Rate, PreferenceSection.Limits);
        Downloads = new(this, "active_downloads", PreferenceKind.Integer, PreferenceSection.Transfers);
        Seeds = new(this, "active_seeds", PreferenceKind.Integer, PreferenceSection.Transfers);
        Connections = new(this, "connection_limit", PreferenceKind.Integer, PreferenceSection.Network);
        Encryption = new(this, "encryption", PreferenceKind.Text, PreferenceSection.Network);
        Proxy = new(this, client);
        Ratio = new(this, "ratio_limit", PreferenceKind.Number, PreferenceSection.Transfers);
        SeedingMinutes = new(this, "seeding_minutes", PreferenceKind.Integer, PreferenceSection.Transfers);
        Interface = new(this, "network_interface", PreferenceKind.Text, PreferenceSection.Network);
        Interfaces = [new(string.Empty, Text.Get("preferences", "any_interface"))];
        Interface.PropertyChanged += (_, _) => UpdateInterface();
        PortMapping = new(this, "port_mapping", PreferenceKind.Boolean, PreferenceSection.Network);
        Port = new(this, "listen_port", PreferenceKind.Port, PreferenceSection.Network);
        ProblemNotifications = new(this, "notify_problems", PreferenceKind.Boolean, PreferenceSection.General);
        FinishedNotifications = new(this, "notifications_enabled", PreferenceKind.Boolean, PreferenceSection.General);
        AddedNotifications = new(this, "notify_added", PreferenceKind.Boolean, PreferenceSection.General);
        PreventSleep = new(this, "prevent_sleep", PreferenceKind.Boolean, PreferenceSection.General);
        SeedingSleep = new(this, "prevent_sleep_seeding", PreferenceKind.Boolean, PreferenceSection.General);
        Updates = new(this, "check_for_updates", PreferenceKind.Boolean, PreferenceSection.General);
        Schedule = new(this);
        Language = new(this, "language", PreferenceKind.Text, PreferenceSection.Appearance);
        Theme = new(this, "theme", PreferenceKind.Text, PreferenceSection.Appearance);
        Schedule.PropertyChanged += (_, _) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPending)));
        Fields = [Destination, ShowAdd, Download, Upload, AlternativeDownload, AlternativeUpload,
            Downloads, Seeds, Connections, Encryption, Ratio, SeedingMinutes, Interface, PortMapping, Port,
            ProblemNotifications, FinishedNotifications, AddedNotifications, PreventSleep, SeedingSleep, Updates,
            ShowSplash, StartInTray, Language, Theme];
        OpenDefaults = new Command(() => Register("open_defaults"), () => CanRegister);
        OpenStartup = new Command(() => Register("open_startup"), () => CanRegister);
    }

    public void RefreshInterfaces()
    {
        try
        {
            var choices = new List<InterfaceChoice> { new(string.Empty, Text.Get("preferences", "any_interface")) };
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                choices.Add(new(adapter.Id, adapter.Name));
            Interfaces = choices;
            _unavailableInterface = null;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Interfaces)));
            UpdateInterface();
        }
        catch (NetworkInformationException error) { Interface.Reject(error); }
    }

    private void UpdateInterface()
    {
        var choices = Interfaces.Where(choice => choice != _unavailableInterface).ToList();
        if (!choices.Any(choice => choice.InterfaceId == Interface.Input))
        {
            var unavailable = _unavailableInterface is { } current && current.InterfaceId == Interface.Input
                ? current : new InterfaceChoice(Interface.Input, Text.Format("preferences", "unavailable_interface", Interface.Input));
            _unavailableInterface = unavailable;
            choices.Add(unavailable);
        }
        else _unavailableInterface = null;
        if (!Interfaces.SequenceEqual(choices))
        {
            Interfaces = choices;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Interfaces)));
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedInterface)));
    }

    // The proxy status is the snapshot's check of the proxy in use.
    internal void Apply(JsonElement settings, JsonElement proxy, JsonElement proxyCheck)
    {
        var changed = Proxy.Apply(settings, proxy, proxyCheck);
        foreach (var field in Fields)
            if (settings.TryGetProperty(field.Name, out var value)) changed |= field.Confirm(value);
        if (settings.TryGetProperty("schedule", out var schedule)) Schedule.Apply(schedule);
        if (changed) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    internal async Task Save(object changes)
    {
        await _client.Send("settings", new { changes });
        var confirmation = JsonSerializer.SerializeToElement(changes);
        var changed = false;
        foreach (var field in Fields)
            if (confirmation.TryGetProperty(field.Name, out var value)) changed |= field.Confirm(value);
        if (changed) Changed();
        _owner.RequestSnapshot();
    }

    internal async Task SaveLanguage(string language)
    {
        Language.Input = language;
        await Submit(Language, language);
        if (Language.Failure is { } error) throw error;
    }

    internal Task HideAddForm() => Save(new { show_add = false });

    public Task ObserveRegistration() => Register("observe");
    public Task SetStartup(bool enabled) => Register(enabled ? "enable_startup" : "disable_startup");
    public Task SetHandlers(bool enabled) => Register(enabled ? "open_defaults" : "unregister_handlers");
    public void SelectLanguage(string language)
    {
        if (CanSelectLanguage) _owner.SelectLanguage(language);
    }

    private async Task Register(string operation)
    {
        if (!CanRegister) return;
        _registering = true;
        _registrationError = null;
        // Every property would also refresh the switches, which would show the
        // state before this change until the engine answers.
        Changed(nameof(CanRegister), nameof(IsPending), nameof(StartupMessage), nameof(HandlersMessage));
        try { _registration = await _client.Send("registration", new { operation }); }
        catch (Exception error)
        {
            _registrationError = (operation, error);
            if (error is CommandFailure && operation != "observe")
                try { _registration = await _client.Send("registration", new { operation = "observe" }); }
                catch (Exception) { }
        }
        finally { _registering = false; Changed(); }
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
            if (!await Depart(field)) saved = false;
        if (!saved) return false;
        while (Fields.FirstOrDefault(field => !CanLeave(field)) is { } remaining)
            if (!await Depart(remaining)) return false;
        return await Schedule.Depart();
    }

    public async Task<bool> Depart(Preference field)
    {
        await field.Saving;
        if (!field.HasDraft) return true;
        if (!TryValue(field, out var value)) { field.Cancel(); return true; }
        if (!_owner.CanSave) return !_owner.IsPicking;
        if (field.Failure is CommandFailure) return false;
        await Submit(field, value);
        return CanLeave(field);
    }

    private bool CanLeave(Preference field) => !field.HasDraft || !_owner.CanSave && !_owner.IsPicking ||
        field.Failure is not null and not CommandFailure;

    public async Task Toggle(Preference field, bool value)
    {
        if (!field.CanEdit || field.IsOn == value) return;
        field.Choose(value);
        if (!field.HasDraft && !field.IsPending) { field.Cancel(); return; }
        await Submit(field, value);
    }

    private async Task Submit(Preference field, object value)
    {
        if (!_owner.CanSave) { field.Reject(new IOException(Text.Get("connection", "unavailable"))); return; }
        var submitted = field.Capture(value);
        if (field.IsPending)
        {
            field.Defer(submitted);
            await field.Saving;
            return;
        }
        field.Begin();
        Changed();
        try
        {
            while (true)
            {
                Exception? failure = null;
                try
                {
                    await Save(new Dictionary<string, object> { [field.Name] = submitted.Value });
                    field.Accept(JsonSerializer.SerializeToElement(submitted.Value), submitted);
                }
                catch (Exception error) { failure = error; }
                if (_owner.CanSave && (failure is null or CommandFailure) && field.TakeIntent() is { } next)
                {
                    submitted = next;
                    continue;
                }
                if (failure is not null) field.Reject(failure, submitted);
                break;
            }
        }
        finally { field.End(); Changed(); }
    }

    private static bool TryValue(Preference field, out object value)
    {
        value = field.Input.Trim();
        if (field.Kind == PreferenceKind.Text) return true;
        if (field.Kind == PreferenceKind.Boolean) { value = field.IsOn; return true; }
        if (field.HasUnlimited && field.Input.Trim().Length == 0)
        {
            value = field.Kind == PreferenceKind.Number ? 0.0 : (object)0;
            return true;
        }
        if (field.IsRate)
        {
            if (!TryRate(field.Input, out var bytes)) return false;
            value = bytes;
            return true;
        }
        if (!double.TryParse(field.Input, NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.CurrentCulture, out var number)) return false;
        if (!double.IsFinite(number) || number < 0) return false;
        if (field.Kind == PreferenceKind.Number) { value = number; return true; }
        if (number != Math.Truncate(number) || number > int.MaxValue ||
            field.Kind == PreferenceKind.Port && (number < 1 || number > 65535)) return false;
        value = (int)number;
        return true;
    }

    // A speed limit is typed in KiB/s and sent in bytes a second.
    internal static bool TryRate(string input, out int bytes)
    {
        bytes = 0;
        if (!double.TryParse(input, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out var number) ||
            !double.IsFinite(number) || number < 0 || number > int.MaxValue / 1024.0) return false;
        bytes = checked((int)Math.Round(number * 1024));
        return true;
    }

    internal static string RateInput(int bytes) => (bytes / 1024.0).ToString("G", CultureInfo.CurrentCulture);

    public Task CancelDraft() => Schedule.Close();

    public void RefreshText()
    {
        foreach (var choice in Interfaces)
            if (choice.InterfaceId.Length == 0) choice.Text = Text.Get("preferences", "any_interface");
        if (_unavailableInterface is { } unavailable)
            unavailable.Text = Text.Format("preferences", "unavailable_interface", unavailable.InterfaceId);
        Changed();
        foreach (var field in Fields) field.Refresh();
        Proxy.Refresh();
        Schedule.RefreshText();
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void Refresh()
    {
        Changed();
        foreach (var field in Fields) field.Refresh();
        Proxy.Refresh();
        Schedule.Refresh();
    }

    // Without names, every property changed.
    internal void Changed(params string[] names)
    {
        foreach (var name in names.Length == 0 ? new[] { string.Empty } : names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        foreach (Command command in new[] { OpenDefaults, OpenStartup }) command.Refresh();
    }

}

public sealed class Preference(Preferences owner, string name, PreferenceKind kind, PreferenceSection section) : INotifyPropertyChanged
{
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
    public string Name { get; } = name;
    public PreferenceKind Kind { get; } = kind;
    public PreferenceSection Section { get; } = section;
    public bool IsRate => Kind == PreferenceKind.Rate;
    // The engine reads 0 as no limit for every number except the port, so
    // an empty field shows Unlimited and saves 0.
    internal bool HasUnlimited => Kind is PreferenceKind.Rate or PreferenceKind.Integer or PreferenceKind.Number;
    internal string ConfirmedText => _confirmedInput;
    internal bool ConfirmedOn => _confirmed.ValueKind == JsonValueKind.True;
    public string Label => owner.Text.Get(IsRate ? "limits" : "preferences", Name);
    public string Input
    {
        get => _input;
        set { if (_input == value) return; _input = value; _cancelled = false; _failure = null; _uncertain = null; _invalid = false; Refresh(); owner.Changed(); }
    }
    public bool IsOn => _choice ?? _confirmed.ValueKind == JsonValueKind.True;
    public bool HasDraft => _input != _confirmedInput || _choice is { } choice && choice != (_confirmed.ValueKind == JsonValueKind.True);
    public bool IsPending => _saving is not null;
    internal Exception? Failure => _failure;
    internal Task Saving => _saving?.Task ?? Task.CompletedTask;
    // A save keeps its control enabled so later input keeps focus.
    public bool CanEdit => owner.CanEdit;
    public string Message => _invalid ? owner.Text.Get(IsRate ? "errors" : "preferences", IsRate ? "invalid_limits" : Kind == PreferenceKind.Port ? "invalid_port" :
        Kind == PreferenceKind.Integer ? "invalid_count" : "invalid_number") :
        _failure is null ? string.Empty :
        owner.Text.Error(_failure);
    public event PropertyChangedEventHandler? PropertyChanged;

    internal bool Confirm(JsonElement value)
    {
        var changed = _confirmed.ValueKind != value.ValueKind || _confirmed.GetRawText() != value.GetRawText();
        if (changed)
        {
            var preserve = HasDraft || IsPending;
            _confirmed = value.Clone();
            _confirmedInput = value.ValueKind == JsonValueKind.String ? value.GetString()! :
                value.ValueKind != JsonValueKind.Number || HasUnlimited && value.GetDouble() == 0 ? string.Empty :
                IsRate ? Preferences.RateInput(value.GetInt32()) : value.GetDouble().ToString("G", CultureInfo.CurrentCulture);
            if (!preserve) { _input = _confirmedInput; _choice = null; }
        }
        if (!IsPending && _uncertain is { } submitted &&
            JsonElement.DeepEquals(value, JsonSerializer.SerializeToElement(submitted.Value)))
        {
            Settle(submitted);
            changed = true;
        }
        if (changed) Refresh();
        return changed;
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
        Confirm(value);
        Settle(submitted);
        Refresh();
    }
    private void Settle(Submission submitted)
    {
        var preserve = !_cancelled && (_input != submitted.Input || _choice != submitted.Choice);
        if (!preserve) { _input = _confirmedInput; _choice = null; _invalid = false; }
        _cancelled = false;
        _failure = null;
        _uncertain = null;
    }
    internal void Begin() { _saving = new(TaskCreationOptions.RunContinuationsAsynchronously); _failure = null; _uncertain = null; _invalid = false; Refresh(); }
    internal void End()
    {
        var saving = _saving;
        _saving = null;
        _intent = null;
        if (_uncertain is null) _cancelled = false;
        Refresh();
        saving?.TrySetResult();
    }
    internal void Reject(Exception error, Submission? submitted = null)
    {
        _failure = error;
        _uncertain = error is CommandFailure ? null : submitted;
        Refresh();
    }
    internal void Invalid() { _invalid = true; Refresh(); }
    internal void Choose(bool value) { _choice = value; _cancelled = false; _failure = null; _uncertain = null; _invalid = false; Refresh(); }
    public void Cancel()
    {
        _intent = null;
        _cancelled = IsPending;
        _input = _confirmedInput;
        _choice = null;
        _failure = null;
        _uncertain = null;
        _invalid = false;
        Refresh();
        owner.Changed();
    }
    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

public sealed class InterfaceChoice(string interfaceId, string text) : INotifyPropertyChanged
{
    private string _text = text;
    public string InterfaceId { get; } = interfaceId;
    public string Text
    {
        get => _text;
        internal set { _text = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public override string ToString() => Text;
}
