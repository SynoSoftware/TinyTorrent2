using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Subtitles;

internal sealed class Settings : UserControl
{
    private Acquisition? _model;
    private readonly Func<Task> _retry;
    private readonly ToggleSwitch _enabled = new();
    private readonly ComboBox _extras = new()
    {
        MinWidth = 80,
        ItemsSource = Enumerable.Range(0, Acquisition.MaximumExtras + 1).ToArray(),
    };
    private readonly Batch _batch;
    private readonly ActionButton _find = new();
    private readonly ActionButton _scan = new() { Glyph = Lucide.FolderSearch };
    private readonly CheckStatus _scanStatus = new();
    private readonly SettingsRow _scanRow;
    private bool _scanning;
    private ScanResult? _scanned;
    private readonly ActionButton _check = new() { Glyph = Lucide.Plug };
    private readonly ActionButton _edit = new() { Glyph = Lucide.Pencil };
    private readonly ActionButton _add = new() { Glyph = Lucide.Plus };
    private readonly Button _information = new()
    {
        Content = new FontIcon
        {
            FontFamily = Lucide.Font,
            Glyph = Lucide.Info,
            FontSize = 16,
        },
        Style = (Style)Application.Current.Resources["TinyTorrentSubtleButtonStyle"],
    };
    private readonly ComboBox _supplier = new() { Width = 280 };
    private readonly AutoSuggestBox _language = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };
    private readonly StackPanel _languages = new() { Spacing = 8, MinWidth = 280 };
    private readonly TextBlock _status = new()
    {
        Style = (Style)Application.Current.Resources["TinyTorrentBodyTextStyle"],
        MaxLines = 1,
    };
    private readonly SettingsSection _section;
    private readonly SettingsRow _automaticRow;
    private readonly SettingsRow _extrasRow;
    private readonly SettingsRow _accessRow;
    private readonly SettingsRow _batchRow;
    private readonly SettingsRow _supplierRow;
    private readonly SettingsRow _languagesRow;
    private readonly SettingsRow _languageInputRow;
    private readonly ProgressRing _progress = new()
    {
        Width = 16,
        Height = 16,
        IsActive = false,
    };
    private bool _checking;

    // Reports a failed action to the window's notifications, with the
    // notification title key that names the action.
    private readonly Action<Exception, string> _report;
    private Task _saving = Task.CompletedTask;
    internal Task Saving => _saving;
    private bool _refreshing;
    private bool _unknownLanguage;
    private Strings Text { get; }
    internal event EventHandler<SubtitleSupplier>? SupplierRequested;

    internal Control? Field(string name) =>
        name switch
        {
            "subtitle_automatic" => _enabled,
            "subtitle_extras" => _extras,
            "subtitle_supplier" => _supplier,
            "subtitle_languages" => _language,
            "subtitle_batch" => _find,
            "subtitle_scan" => _scan,
            _ => null,
        };

    internal Settings(Strings text, Func<Task> retry, Action<Exception, string> report)
    {
        Text = text;
        _retry = retry;
        _report = report;
        AutomationProperties.SetAutomationId(_enabled, "SubtitleAutomatic");
        AutomationProperties.SetAutomationId(_extras, "SubtitleExtras");
        _batch = new Batch(text, report);
        AutomationProperties.SetAutomationId(_find, "SubtitleFind");
        AutomationProperties.SetAutomationId(_scan, "SubtitleScan");
        _batch.PropertyChanged += (_, _) =>
        {
            _find.IsEnabled = _batch.CanAct;
            _find.Glyph = _batch.ActionGlyph;
            _find.Text = _batch.ActionText;
            ToolTipService.SetToolTip(_find, _batch.ActionHint);
        };
        _find.Click += async (_, _) => await _batch.Act();
        _scan.Click += async (_, _) => await Scan();
        AutomationProperties.SetAutomationId(_check, "SubtitleCheck");
        AutomationProperties.SetAutomationId(_edit, "SubtitleEdit");
        AutomationProperties.SetAutomationId(_add, "SubtitleAddLanguage");
        AutomationProperties.SetAutomationId(_information, "SubtitleSupplierInformation");
        AutomationProperties.SetAutomationId(_supplier, "SubtitleSupplier");
        AutomationProperties.SetAutomationId(_language, "SubtitleLanguage");
        _automaticRow = new SettingsRow { Content = _enabled };
        _extrasRow = new SettingsRow { Content = _extras };
        _supplierRow = new SettingsRow
        {
            Content = Pair(
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { _supplier, _information },
                },
                _edit
            ),
        };
        var status = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _status, _progress },
        };
        _accessRow = new SettingsRow { Content = Pair(status, _check) };
        _languageInputRow = new SettingsRow { Content = Pair(_language, _add) };
        _languagesRow = new SettingsRow
        {
            Content = _languages,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        _batchRow = new SettingsRow { Content = Pair(_batch, _find) };
        _scanStatus.Check.Visibility = Visibility.Collapsed;
        _scanRow = new SettingsRow { Content = Pair(_scanStatus, _scan) };
        _section = new SettingsSection
        {
            Glyph = Lucide.Captions,
            Content = new StackPanel
            {
                Spacing = 16,
                Children =
                {
                    new StackPanel { Children = { _supplierRow, _accessRow } },
                    new StackPanel { Children = { _languageInputRow, _languagesRow } },
                    new StackPanel { Children = { _automaticRow, _extrasRow } },
                    Divider(),
                    _batchRow,
                    Divider(),
                    _scanRow,
                },
            },
        };
        Content = _section;
        _enabled.Toggled += async (_, _) =>
        {
            if (!_refreshing && _model is { } model)
            {
                var enabled = _enabled.IsOn;
                await Save(() => model.SetEnabled(enabled));
            }
        };
        _check.Click += async (_, _) => await Check();
        _extras.SelectionChanged += async (_, _) =>
        {
            if (!_refreshing && _model is { } model && _extras.SelectedItem is int extras)
                await Save(() => model.SetExtras(extras));
        };
        _edit.Click += (_, _) =>
        {
            if (_model is { } model)
                SupplierRequested?.Invoke(this, model.SupplierId);
        };
        _supplier.SelectionChanged += async (_, _) =>
        {
            if (
                _refreshing
                || _model is not { } model
                || _supplier.SelectedItem is not SubtitleSupplier chosen
                || chosen == model.SupplierId
            )
                return;
            await Save(() => model.SelectSupplier(chosen));
        };
        _add.Click += async (_, _) => await AddLanguage();
        _language.QuerySubmitted += async (_, args) =>
            await AddLanguage(args.ChosenSuggestion as LanguageChoice);
        _language.TextChanged += (_, args) =>
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput && _model is { } model)
                _language.ItemsSource = (model.Supplier?.Languages ?? [])
                    .Select(Choice)
                    .Where(choice =>
                        choice.Name.Contains(
                            _language.Text,
                            StringComparison.CurrentCultureIgnoreCase
                        )
                        || choice.Tag.StartsWith(_language.Text, StringComparison.OrdinalIgnoreCase)
                    )
                    .ToArray();
        };
        _information.Click += (_, _) =>
        {
            if (_model?.Supplier is { } supplier)
                SupplierDialog.Information(Text, supplier).ShowAt(_information);
        };
        Unloaded += (_, _) => Attach(null);
        RefreshText();
    }

    internal void Attach(Acquisition? model)
    {
        if (_model == model)
            return;
        if (_model is not null)
            _model.Changed -= OnChanged;
        _model = model;
        _scanned = null;
        _batch.Attach(model);
        if (_model is not null)
            _model.Changed += OnChanged;
        Refresh();
    }

    private void OnChanged(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(Refresh);

    private string T(string key) => Text.Get("subtitles", key);

    private string F(string key, params object[] values) => Text.Format("subtitles", key, values);

    internal void RefreshText()
    {
        _section.Header = Text.Get("settings", "subtitles");
        _automaticRow.Header = T("automatic");
        _automaticRow.Description = T("automatic_hint");
        _extrasRow.Header = T("extras");
        _extrasRow.Description = T("extras_hint");
        _supplierRow.Header = T("supplier");
        _accessRow.Header = T("access");
        _languageInputRow.Header = T("add_language");
        _languagesRow.Header = T("language_selection");
        _batchRow.Header = T("batch");
        _scanRow.Header = T("files");
        _scan.Text = T("scan");
        ToolTipService.SetToolTip(_scan, T("scan_hint"));
        _batch.Refresh();
        _check.Text = T("check");
        _edit.Text = T("edit");
        _add.Text = T("add");
        ToolTipService.SetToolTip(_check, T("check_hint"));
        ToolTipService.SetToolTip(_edit, T("edit_hint"));
        ToolTipService.SetToolTip(_add, T("add_language"));
        AutomationProperties.SetName(_language, T("languages"));
        AutomationProperties.SetName(_enabled, T("automatic"));
        AutomationProperties.SetName(_extras, T("extras"));
        AutomationProperties.SetHelpText(_extras, T("extras_hint"));
        AutomationProperties.SetName(_supplier, T("supplier"));
        ToolTipService.SetToolTip(_supplier, T("supplier_hint"));
        AutomationProperties.SetName(_information, T("supplier_information"));
        ToolTipService.SetToolTip(_information, T("supplier_information"));
        _language.PlaceholderText = T("language_hint");
        Refresh();
    }

    private void Refresh()
    {
        _refreshing = true;
        if (_saving.IsCompleted)
        {
            _enabled.IsOn = _model?.Enabled == true;
            _extras.SelectedItem = _model?.Extras;
        }
        _automaticRow.StateText = SettingsRow.State(
            _enabled.IsOn,
            Text.Get("settings", "on"),
            Text.Get("settings", "off")
        );
        _enabled.IsEnabled = _model is not null;
        _extras.IsEnabled = _model is not null;
        _scan.IsEnabled = _model is not null;
        var scanned =
            _scanning ? T("scanning")
            : _scanned is { } result ? F("scanned", result.Found, result.Unreadable)
            : string.Empty;
        _scanStatus.Visibility =
            _scanning || _scanned is not null ? Visibility.Visible : Visibility.Collapsed;
        _scanStatus.Refresh(
            _scanning,
            _scanned is { } scan ? scan.Unreadable == 0 : null,
            scanned,
            scanned
        );
        _check.IsEnabled = _model?.CanCheck == true;
        _edit.IsEnabled = _model is not null && !_checking;
        _language.IsEnabled = _add.IsEnabled = _model is not null;
        _supplier.ItemsSource ??= _model
            ?.Suppliers.Select(supplier => supplier.SupplierId)
            .ToArray();
        _supplier.SelectedItem = _model?.SupplierId;
        _supplier.IsEnabled = _model is not null && !_checking;
        _progress.IsActive = _checking;
        _progress.Visibility = _checking ? Visibility.Visible : Visibility.Collapsed;
        // Problems go to the window's message bar and notifications; this line
        // states only the supplier's access and allowance.
        _status.Text =
            _checking ? Text.Get("settings", "proxy_checking")
            : _model?.RetryAt is { } reset && reset > DateTimeOffset.UtcNow
                ? F("retry_at", Text.Time(reset))
            : _model?.Checked == true ? T("connected")
            : T("not_checked");
        ToolTipService.SetToolTip(_status, _status.Text);
        AutomationProperties.SetHelpText(_status, _status.Text);
        _languageInputRow.Error = _unknownLanguage ? T("choose_language") : string.Empty;
        RefreshLanguages();
        _refreshing = false;
    }

    private void RefreshLanguages()
    {
        var tags = _model?.EffectiveLanguages ?? [];
        var signature =
            string.Join(",", tags)
            + ":"
            + _model?.SupplierId
            + ":"
            + Text.Language
            + ":"
            + (_model?.Languages.Length == 0);
        if (Equals(_languages.Tag, signature))
            return;
        _languages.Tag = signature;
        _languages.Children.Clear();
        foreach (var tag in tags)
        {
            var name = Text.LanguageName(tag);
            if (_model is { } model && model.Supplier?.Supports(tag) != true)
                name += " · " + F("unsupported", model.SupplierId);
            var remove = new Button
            {
                Content = new FontIcon { FontFamily = Lucide.Font, Glyph = Lucide.X },
                Style = (Style)Application.Current.Resources["TinyTorrentSubtleButtonStyle"],
            };
            AutomationProperties.SetName(remove, F("remove", Text.LanguageName(tag)));
            AutomationProperties.SetAutomationId(remove, "SubtitleRemoveLanguage-" + tag);
            ToolTipService.SetToolTip(remove, F("remove", Text.LanguageName(tag)));
            remove.IsEnabled = _model?.Languages.Length > 0;
            var index = _languages.Children.Count;
            // The removed row leaves the page with its button, so focus moves
            // to the row that takes its place, or to Add, never to a text box.
            remove.Click += async (_, _) =>
            {
                if (
                    _model is not { } saved
                    || !await Save(() =>
                        saved.SetLanguages(saved.Languages.Where(value => value != tag))
                    )
                )
                    return;
                var buttons = _languages
                    .Children.OfType<Grid>()
                    .Select(row => row.Children[1])
                    .OfType<Button>()
                    .Where(button => button.IsEnabled)
                    .ToArray();
                Control next =
                    buttons.Length > 0 ? buttons[Math.Min(index, buttons.Length - 1)] : _add;
                next.Focus(FocusState.Programmatic);
            };
            _languages.Children.Add(
                Pair(
                    new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis },
                    remove
                )
            );
        }
    }

    private async Task AddLanguage(LanguageChoice? selected = null)
    {
        if (_model is not { } model)
            return;
        var input = _language.Text;
        selected ??= (model.Supplier?.Languages ?? [])
            .Select(Choice)
            .FirstOrDefault(choice =>
                string.Equals(choice.Tag, _language.Text.Trim(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    choice.Name,
                    _language.Text.Trim(),
                    StringComparison.CurrentCultureIgnoreCase
                )
            );
        if (selected is null)
        {
            _unknownLanguage = true;
            Refresh();
            _language.Focus(FocusState.Programmatic);
            return;
        }
        // Adding to the interface-language default keeps that language, which
        // the person sees in the list.
        if (
            await Save(() => model.SetLanguages(model.EffectiveLanguages.Append(selected.Tag)))
            && _language.Text == input
        )
            _language.Text = string.Empty;
    }

    private async Task<bool> Save(Func<Task> action)
    {
        var model = _model;
        var previous = _saving;
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        _saving = completion.Task;
        await previous;
        _unknownLanguage = false;
        try
        {
            if (model is null || model.IsDisposed)
                return false;
            await action();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception error)
        {
            if (CanReport(model))
                _report(error, "failed_settings");
            return false;
        }
        finally
        {
            completion.SetResult(true);
            Refresh();
        }
    }

    private async Task Check()
    {
        if (_checking || _model is not { CanCheck: true } model)
            return;
        _checking = true;
        Refresh();
        try
        {
            await _retry();
            if (_model is not { CanCheck: true } current)
                return;
            model = current;
            await model.Check();
        }
        catch (OperationCanceledException) { }
        // The status line already says when a used-up allowance returns.
        catch (SubtitleException error)
            when (error.Reason == SubtitleFailure.Quota && _model?.RetryAt > DateTimeOffset.UtcNow)
        { }
        catch (Exception error)
        {
            if (CanReport(model))
                _report(error, "failed_subtitle_check");
        }
        finally
        {
            _checking = false;
            Refresh();
        }
    }

    private async Task Scan()
    {
        if (_scanning || _model is not { } model)
            return;
        _scanning = true;
        _scanned = null;
        Refresh();
        try
        {
            var result = await model.Rescan();
            if (ReferenceEquals(model, _model))
                _scanned = result;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (CanReport(model))
                _report(error, "failed_subtitle_scan");
        }
        finally
        {
            _scanning = false;
            Refresh();
        }
    }

    private bool CanReport(Acquisition? model) =>
        model is { IsDisposed: false } && (_model is null || ReferenceEquals(model, _model));

    private static Grid Pair(FrameworkElement value, FrameworkElement action)
    {
        var grid = new Grid
        {
            ColumnSpacing = 12,
            MinWidth = 280,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        grid.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
        );
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        value.VerticalAlignment = action.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(action, 1);
        grid.Children.Add(value);
        grid.Children.Add(action);
        return grid;
    }

    internal void Align(double width)
    {
        ActionButton.Align([_find, _check, _edit, _add, _scan]);
        foreach (
            var row in new[]
            {
                _automaticRow,
                _extrasRow,
                _supplierRow,
                _accessRow,
                _languageInputRow,
                _languagesRow,
                _batchRow,
                _scanRow,
            }
        )
            row.Align(0, width);
        _languages.Width = width;
        foreach (
            var row in new[] { _supplierRow, _accessRow, _languageInputRow, _batchRow, _scanRow }
        )
            ((FrameworkElement)row.Content).Width = width;
    }

    private static Border Divider() =>
        new() { Style = (Style)Application.Current.Resources["InspectorDividerStyle"] };

    private LanguageChoice Choice(string tag) => new(tag, Text.LanguageName(tag));

    private sealed record LanguageChoice(string Tag, string Name)
    {
        public override string ToString() => Name;
    }
}
