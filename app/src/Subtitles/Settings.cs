using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Subtitles;

internal sealed partial class Settings : UserControl
{
    private Acquisition? _model;
    private readonly Func<Task> _retry;
    private readonly ToggleSwitch _enabled = new();
    private readonly ActionButton _find = new() { Glyph = Lucide.Search };
    private readonly ActionButton _recheck = new() { Glyph = Lucide.RefreshCw };
    private readonly ActionButton _check = new() { Glyph = Lucide.Plug };
    private readonly ActionButton _edit = new() { Glyph = Lucide.Pencil };
    private readonly ActionButton _add = new() { Glyph = Lucide.Plus };
    private readonly ActionButton _reset = new() { Glyph = Lucide.RotateCcw };
    private readonly ActionButton _supplier = new() { Glyph = Lucide.Info };
    private readonly AutoSuggestBox _language = new() { MinWidth = 180, MaxWidth = 320, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly StackPanel _languages = new() { Spacing = 8 };
    private readonly TextBlock _missing = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _checked = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _status = new() { Style = (Style)Application.Current.Resources["TinyTorrentBodyTextStyle"], MaxLines = 1 };
    private readonly TextBlock _failures = new() { Style = (Style)Application.Current.Resources["TinyTorrentBodyTextStyle"] };
    private readonly SettingsSection _automatic;
    private readonly SettingsSection _access;
    private readonly SettingsSection _wanted;
    private readonly SettingsSection _existing;
    private readonly SettingsRow _automaticRow;
    private readonly SettingsRow _finishedRow;
    private readonly SettingsRow _filesRow;
    private readonly SettingsRow _supplierRow;
    private Task _saving = Task.CompletedTask;
    private bool _refreshing;
    private bool _busy;
    private string _errorKey = string.Empty;
    private Exception? _failure;
    private Strings Text { get; }
    internal event EventHandler? SupplierRequested;
    internal event EventHandler? ProblemChanged;
    private SubtitleFailure? PersistentFailure => _model is
        { Failure: not SubtitleFailure.None and not SubtitleFailure.Quota and not SubtitleFailure.Network } failed
        ? failed.Failure : null;
    internal string Problem => _failures.Text.Length > 0 ? _failures.Text :
        _model?.RetryAt > DateTimeOffset.UtcNow || PersistentFailure is not null
            ? _status.Text : string.Empty;
    internal IEnumerable<ActionButton> Actions => [_find, _recheck, _check, _edit, _add, _reset];

    internal Control? Field(string name) => name switch
    {
        "subtitle_automatic" => _enabled,
        "subtitle_supplier" => _edit,
        "subtitle_languages" => _language,
        "subtitle_finished" => _find,
        "subtitle_files" => _recheck,
        _ => null,
    };

    internal Settings(Strings text, Func<Task> retry)
    {
        Text = text;
        _retry = retry;
        AutomationProperties.SetAutomationId(_enabled, "SubtitleAutomatic");
        AutomationProperties.SetAutomationId(_find, "SubtitleFind");
        AutomationProperties.SetAutomationId(_recheck, "SubtitleRecheck");
        AutomationProperties.SetAutomationId(_check, "SubtitleCheck");
        AutomationProperties.SetAutomationId(_edit, "SubtitleEdit");
        AutomationProperties.SetAutomationId(_add, "SubtitleAddLanguage");
        AutomationProperties.SetAutomationId(_reset, "SubtitleResetLanguages");
        AutomationProperties.SetAutomationId(_supplier, "SubtitleSupplierInformation");
        AutomationProperties.SetAutomationId(_language, "SubtitleLanguage");
        _automaticRow = new SettingsRow { Content = _enabled };
        _finishedRow = new SettingsRow { Content = Pair(_missing, _find) };
        _filesRow = new SettingsRow { Content = Pair(_checked, _recheck) };
        _supplierRow = new SettingsRow { Content = Pair(_supplier, _edit) };
        _automatic = Section(Lucide.Captions, _automaticRow);
        _access = Section(Lucide.Network, _supplierRow, Pair(_status, _check), _failures);
        _wanted = Section(Lucide.Languages, Pair(_language, _add), _languages, _reset);
        _existing = Section(Lucide.FolderSearch, _finishedRow, _filesRow);
        _reset.HorizontalAlignment = HorizontalAlignment.Right;
        Content = new StackPanel { Spacing = 16, Children = { _automatic, _access, _wanted, _existing } };
        _enabled.Toggled += async (_, _) =>
        {
            if (!_refreshing && _model is { } model)
            {
                var enabled = _enabled.IsOn;
                await Save(() => model.SetEnabled(enabled));
            }
        };
        _find.Click += async (_, _) => { if (_model is { } model) await Run(model.Find); };
        _recheck.Click += async (_, _) => { if (_model is { } model) await Run(model.Recheck); };
        _check.Click += async (_, _) =>
        {
            if (_model is { } model)
                await Run(async () =>
                {
                    await _retry();
                    await model.Check();
                });
        };
        _edit.Click += (_, _) => SupplierRequested?.Invoke(this, EventArgs.Empty);
        _reset.Click += async (_, _) => { if (_model is { } model) await Save(() => model.SetLanguages([])); };
        _add.Click += async (_, _) => await AddLanguage();
        _language.QuerySubmitted += async (_, args) => await AddLanguage(args.ChosenSuggestion as LanguageChoice);
        _language.TextChanged += (_, args) =>
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput && _model is { } model)
                _language.ItemsSource = (model.Supplier?.Languages ?? []).Select(Choice)
                    .Where(choice => choice.Name.Contains(_language.Text, StringComparison.CurrentCultureIgnoreCase) ||
                        choice.Tag.StartsWith(_language.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
        };
        _supplier.Click += (_, _) => { if (_model?.Supplier is { } supplier) SupplierDialog.Information(Text, supplier).ShowAt(_supplier); };
        ActualThemeChanged += (_, _) => Refresh();
        Unloaded += (_, _) =>
        {
            if (_model is not null)
                _model.Changed -= OnChanged;
            _model = null;
        };
        Refresh();
    }

    internal void Attach(Acquisition? model)
    {
        if (_model == model)
            return;
        if (_model is not null)
            _model.Changed -= OnChanged;
        _model = model;
        if (_model is not null)
            _model.Changed += OnChanged;
        Refresh();
    }

    private void OnChanged(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(Refresh);
    private string T(string key) => Text.Get("subtitles", key);
    private string F(string key, params object[] values) => Text.Format("subtitles", key, values);

    private void RefreshText()
    {
        _automatic.Header = T("automatic");
        _access.Header = T("supplier");
        _wanted.Header = T("languages");
        _existing.Header = T("existing");
        _automaticRow.Header = T("automatic");
        _automaticRow.Description = T("automatic_hint");
        _finishedRow.Header = T("finished");
        _filesRow.Header = T("files");
        _filesRow.Description = T("recheck_hint");
        _supplierRow.Header = T("supplier");
        Label(_find, "find"); Label(_recheck, "recheck"); Label(_check, "check");
        Label(_edit, "edit"); Label(_add, "add"); Label(_reset, "reset");
        AutomationProperties.SetName(_language, T("languages"));
        AutomationProperties.SetName(_enabled, T("automatic"));
        ToolTipService.SetToolTip(_reset, T("reset_hint"));
        _language.PlaceholderText = T("language_hint");
    }

    internal void Refresh()
    {
        _refreshing = true;
        RefreshText();
        if (_saving.IsCompleted)
            _enabled.IsOn = _model?.Enabled == true;
        _automaticRow.StateText = SettingsRow.State(_enabled.IsOn, Text.Get("settings", "on"), Text.Get("settings", "off"));
        _enabled.IsEnabled = _model?.Enabled == true || _model?.Ready == true;
        ToolTipService.SetToolTip(_enabled, _model is { Ready: false }
            ? T(_model.Supplier?.Available == true ? "failure_unconfigured" : "failure_unavailable") : T("automatic_hint"));
        _find.IsEnabled = !_busy && _model is { Enabled: true, Ready: true, Rechecking: false } model && model.Missing.Values.Sum() > 0;
        _recheck.IsEnabled = !_busy && _model is { Rechecking: false, Finding: false };
        _check.IsEnabled = _edit.IsEnabled = _model is not null && !_busy;
        _wanted.IsEnabled = _model is not null;
        _reset.IsEnabled = _model?.Languages.Length > 0;
        _supplier.Text = _model?.SupplierId.ToString() ?? SubtitleSupplier.OpenSubtitles.ToString();
        _missing.Text = _model is { Pending: > 0 } running ? F("left", running.Pending) :
            _model is { Found: { } found } completed ? F("found", found, completed.FindTotal) :
            F("missing", _model?.Missing.Values.Sum() ?? 0);
        _checked.Text = _model is { Rechecking: true } checking ? F("checking", checking.Rechecked, checking.RecheckTotal) :
            _model?.LastRecheck is { } date ? F("last_checked", Text.Time(date)) : T("not_checked");
        _status.Text = _errorKey.Length > 0 ? T(_errorKey) : _failure is { } operationFailure ? Text.Error(operationFailure) :
            _model?.RetryAt is { } reset && reset > DateTimeOffset.UtcNow
            ? F("retry_at", Text.Time(reset)) :
            PersistentFailure is { } failure
                ? T("failure_" + failure.ToString().ToLowerInvariant()) : _model?.Checked == true ? T("connected") : T("not_checked");
        _status.Foreground = (Brush)Application.Current.Resources[
            _errorKey.Length > 0 || _failure is not null || _model?.RetryAt > DateTimeOffset.UtcNow || PersistentFailure is not null
                ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush"];
        _failures.Text = _model is { } failures ? string.Join(Environment.NewLine, failures.FileFailures.Select(failure =>
            F("file_failure", failure.Key, T("failure_" + failure.Value.ToString().ToLowerInvariant())))) : string.Empty;
        _failures.Visibility = _failures.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _failures.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        ToolTipService.SetToolTip(_status, _status.Text);
        AutomationProperties.SetHelpText(_status, _status.Text);
        ToolTipService.SetToolTip(_failures, _failures.Text);
        AutomationProperties.SetHelpText(_failures, _failures.Text);
        if (_model is { } current)
        {
            var breakdown = string.Join(", ", current.Missing.Select(pair => Text.LanguageName(pair.Key) + " " + pair.Value));
            var hint = current.Enabled ? F("find_hint", current.Missing.Values.Sum(), breakdown) : T("enable_hint");
            ToolTipService.SetToolTip(_find, hint);
            ToolTipService.SetToolTip(_missing, hint);
        }
        RefreshLanguages();
        _refreshing = false;
        ProblemChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshLanguages()
    {
        var tags = _model?.EffectiveLanguages ?? [];
        var signature = string.Join(",", tags) + ":" + _model?.SupplierId + ":" + Text.Language + ":" + (_model?.Languages.Length == 0);
        if (Equals(_languages.Tag, signature))
            return;
        _languages.Tag = signature;
        _languages.Children.Clear();
        foreach (var tag in tags)
        {
            var name = Text.LanguageName(tag);
            if (_model?.Languages.Length == 0)
                name += " · " + T("interface_language");
            if (_model is { } model && model.Supplier?.Supports(tag) != true)
                name += " · " + F("unsupported", model.SupplierId);
            var remove = new Button { Content = new FontIcon { FontFamily = Lucide.Font, Glyph = Lucide.X },
                Style = (Style)Application.Current.Resources["TinyTorrentSubtleButtonStyle"] };
            AutomationProperties.SetName(remove, F("remove", Text.LanguageName(tag)));
            AutomationProperties.SetAutomationId(remove, "SubtitleRemoveLanguage-" + tag);
            ToolTipService.SetToolTip(remove, F("remove", Text.LanguageName(tag)));
            remove.IsEnabled = _model?.Languages.Length > 0;
            remove.Click += async (_, _) =>
            {
                if (_model is { } saved)
                    await Save(() => saved.SetLanguages(saved.Languages.Where(value => value != tag)));
            };
            _languages.Children.Add(Pair(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis }, remove));
        }
    }

    private async Task AddLanguage(LanguageChoice? selected = null)
    {
        if (_model is not { } model)
            return;
        var input = _language.Text;
        selected ??= (model.Supplier?.Languages ?? []).Select(Choice).FirstOrDefault(choice =>
            string.Equals(choice.Tag, _language.Text.Trim(), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(choice.Name, _language.Text.Trim(), StringComparison.CurrentCultureIgnoreCase));
        if (selected is null)
        {
            _errorKey = "choose_language";
            _failure = null;
            Refresh();
            return;
        }
        if (await Save(() => model.SetLanguages(model.Languages.Append(selected.Tag))) && _language.Text == input)
            _language.Text = string.Empty;
    }

    private async Task<bool> Save(Func<Task> action)
    {
        var previous = _saving;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _saving = completion.Task;
        await previous;
        _failure = null;
        _errorKey = string.Empty;
        try
        {
            await action();
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception error) { _failure = error; return false; }
        finally
        {
            completion.SetResult(true);
            Refresh();
        }
    }

    private async Task Run(Func<Task> action)
    {
        if (_busy)
            return;
        _busy = true;
        _errorKey = string.Empty;
        _failure = null;
        Refresh();
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (SubtitleException error)
        {
            _failure = error.Reason == SubtitleFailure.Quota && _model?.RetryAt > DateTimeOffset.UtcNow
                ? null : error;
        }
        catch (Exception error) { _failure = error; }
        finally { _busy = false; Refresh(); }
    }

    private void Label(ActionButton button, string key) => button.Text = T(key);

    internal static Grid Pair(FrameworkElement value, FrameworkElement action)
    {
        var grid = new Grid { ColumnSpacing = 12, MinWidth = 280, HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        value.VerticalAlignment = action.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(action, 1);
        grid.Children.Add(value);
        grid.Children.Add(action);
        return grid;
    }

    private static SettingsSection Section(string glyph, params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (var child in children)
            panel.Children.Add(child);
        return new SettingsSection { Glyph = glyph, Content = panel };
    }

    private LanguageChoice Choice(string tag) => new(tag, Text.LanguageName(tag));
    private sealed record LanguageChoice(string Tag, string Name)
    {
        public override string ToString() => Name;
    }
}
