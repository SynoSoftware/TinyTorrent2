using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Shared;

namespace Syno.TinyTorrent.Subtitles;

internal sealed class Settings : UserControl
{
    private Acquisition? _model;
    private readonly Func<Task> _retry;
    private readonly ComboBox _extras = new() { Width = 260 };
    private readonly Batch _batch;
    private readonly ActionButton _find = new();
    private readonly ActionButton _scan = new() { Glyph = Lucide.FolderSearch };
    private readonly CheckStatus _scanStatus = new();
    private readonly SettingsRow _scanRow;
    private bool _scanning;
    private ScanResult? _scanned;
    private readonly ActionButton _check = new() { Glyph = Lucide.Plug };
    private readonly ActionButton _edit = new() { Glyph = Lucide.Pencil };
    private readonly Button _information = new()
    {
        Content = new FontIcon
        {
            FontFamily = Lucide.Font,
            Glyph = Lucide.Info,
            FontSize = (double)Application.Current.Resources["TinyTorrentButtonIconSize"],
        },
        Style = (Style)Application.Current.Resources["TinyTorrentSubtleButtonStyle"],
    };
    private readonly ComboBox _supplier = new() { Width = 260 };
    private readonly AutoSuggestBox _language = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };
    private readonly RadioButtons _languageMode = new() { MaxColumns = 2 };
    private readonly RadioButton _languageDefault = new();
    private readonly RadioButton _languageCustom = new();
    private readonly ListView _languages = new()
    {
        SelectionMode = ListViewSelectionMode.Multiple,
        IsMultiSelectCheckBoxEnabled = true,
        MaxHeight = 176,
        ItemsPanel = (ItemsPanelTemplate)Application.Current.Resources["TinyTorrentLanguagePanel"],
    };
    private readonly StackPanel _languageChoices;
    private readonly Expander _wanted;
    private readonly SettingsRow _languageRow = new() { BorderThickness = new Thickness(0) };
    private readonly TextBlock _selection = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _selected = new()
    {
        Style = (Style)Application.Current.Resources["TinyTorrentTitleDetailTextStyle"],
    };
    private readonly SelectorBar _tasks = new();
    private readonly SelectorBarItem _preferencesTab = new();
    private readonly SelectorBarItem _searchTab = new();
    private readonly SelectorBarItem _filesTab = new();
    private readonly Grid _pages = new() { MinHeight = 320 };
    private readonly StackPanel _preferences;
    private readonly Motion _motion = new();
    private readonly TextBlock _status = new()
    {
        Style = (Style)Application.Current.Resources["TinyTorrentBodyTextStyle"],
        MaxLines = 1,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };
    private readonly SettingsSection _section;
    private readonly Automatic _automatic;
    private readonly SettingsRow _extrasRow;
    private readonly SettingsRow _accessRow;
    private readonly SettingsRow _supplierRow;
    private readonly ProgressRing _progress = new()
    {
        Width = (double)Application.Current.Resources["TinyTorrentInlineIconSize"],
        Height = (double)Application.Current.Resources["TinyTorrentInlineIconSize"],
        IsActive = false,
    };
    private bool _checking;

    // Reports a failed action to the window's notifications, with the
    // notification title key that names the action.
    private readonly Action<Exception, string> _report;
    private bool Saving => _model?.Saving.IsCompleted == false;
    private bool _refreshing;
    private bool _visible;
    private Strings Text { get; }
    internal event EventHandler<SubtitleSupplier>? SupplierRequested;

    internal Control? Field(string name)
    {
        (SelectorBarItem? task, Control? field) = name switch
        {
            "subtitle_automatic" => (_preferencesTab, _automatic.Field),
            "subtitle_extras" => (_preferencesTab, _extras),
            "subtitle_supplier" => (_preferencesTab, _supplier),
            "subtitle_languages" => (_preferencesTab, _languageMode),
            "subtitle_batch" => (_searchTab, _find),
            "subtitle_scan" => (_filesTab, _scan),
            _ => (null, null),
        };
        if (task is not null)
            _tasks.SelectedItem = task;
        return field;
    }

    internal Settings(Strings text, Func<Task> retry, Action<Exception, string> report)
    {
        Text = text;
        _retry = retry;
        _report = report;
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
        AutomationProperties.SetAutomationId(_information, "SubtitleSupplierInformation");
        AutomationProperties.SetAutomationId(_supplier, "SubtitleSupplier");
        AutomationProperties.SetAutomationId(_language, "SubtitleLanguage");
        AutomationProperties.SetLiveSetting(
            _selected,
            Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite
        );
        _automatic = new Automatic(text, report);
        for (var count = 0; count <= Acquisition.MaximumExtras; count++)
            _extras.Items.Add(new ComboBoxItem { Tag = count });
        _extrasRow = new SettingsRow { Content = _extras, BorderThickness = new Thickness(0) };
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
        var status = new Grid { ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
        status.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
        );
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_progress, 1);
        status.Children.Add(_status);
        status.Children.Add(_progress);
        _accessRow = new SettingsRow { Content = Pair(status, _check) };
        _languageMode.Items.Add(_languageDefault);
        _languageMode.Items.Add(_languageCustom);
        _languageChoices = new StackPanel
        {
            Spacing = 8,
            Children = { _language, _languages, _selected },
        };
        _languageRow.Content = _selection;
        _wanted = new SettingsExpander
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Header = _languageRow,
            Padding = new Thickness(0, 0, 0, 16),
            Content = new StackPanel
            {
                Spacing = 12,
                Children = { _languageMode, _languageChoices },
            },
        };
        _wanted.Resources["ExpanderHeaderBackground"] =
            new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _wanted.Resources["ExpanderContentBackground"] =
            new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _wanted.Resources["ExpanderHeaderBorderThickness"] = new Thickness(0);
        _wanted.Resources["ExpanderContentDownBorderThickness"] = new Thickness(0);
        _wanted.Resources["ExpanderContentUpBorderThickness"] = new Thickness(0);
        _wanted.Resources["ExpanderHeaderPadding"] = new Thickness(0);
        ScrollViewer.SetHorizontalScrollMode(_languages, ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(_languages, ScrollBarVisibility.Disabled);
        _languages.Loaded += (_, _) =>
        {
            if (_languages.ItemsPanelRoot is { } panel)
            {
                panel.SizeChanged -= ResizeLanguages;
                panel.SizeChanged += ResizeLanguages;
            }
            AlignLanguages();
        };
        _batch.ShowSettings(_find);
        _scanStatus.Check.Visibility = Visibility.Collapsed;
        _scanStatus.MinHeight = 20;
        _scanRow = new SettingsRow
        {
            Content = Pair(_scanStatus, _scan),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        _preferences = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Top,
            Children = { _supplierRow, _accessRow, _wanted, _automatic, _extrasRow },
        };
        _batch.VerticalAlignment = VerticalAlignment.Top;
        AutomationProperties.SetAutomationId(_tasks, "SubtitleTasks");
        AutomationProperties.SetAutomationId(_preferencesTab, "SubtitlePreferencesTab");
        AutomationProperties.SetAutomationId(_searchTab, "SubtitleSearchTab");
        AutomationProperties.SetAutomationId(_filesTab, "SubtitleFilesTab");
        AutomationProperties.SetAutomationId(_preferences, "SubtitlePreferencesPanel");
        AutomationProperties.SetAutomationId(_batch, "SubtitleSearchPanel");
        AutomationProperties.SetAutomationId(_scanRow, "SubtitleFilesPanel");
        _tasks.Items.Add(_preferencesTab);
        _tasks.Items.Add(_searchTab);
        _tasks.Items.Add(_filesTab);
        _pages.Children.Add(_preferences);
        _pages.Children.Add(_batch);
        _pages.Children.Add(_scanRow);
        _batch.Visibility = _scanRow.Visibility = Visibility.Collapsed;
        _pages.SizeChanged += Motion.Clip;
        _motion.Replace(_preferences);
        _tasks.SelectedItem = _preferencesTab;
        _tasks.SelectionChanged += OnTask;
        _section = new SettingsSection
        {
            Glyph = Lucide.Captions,
            Content = new StackPanel
            {
                Spacing = 8,
                Children = { _tasks, _pages },
            },
        };
        Content = _section;
        _check.Click += async (_, _) => await Check();
        _extras.SelectionChanged += async (_, _) =>
        {
            if (
                !_refreshing
                && _model is { } model
                && _extras.SelectedItem is ComboBoxItem { Tag: int extras }
            )
                await Save(model.SetExtras(extras));
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
            )
                return;
            await Save(model.SelectSupplier(chosen));
        };
        _languageMode.SelectionChanged += async (_, _) =>
        {
            if (_refreshing || _model is not { } model)
                return;
            await Save(model.FollowLanguage(_languageMode.SelectedIndex == 0));
        };
        _language.TextChanged += (_, _) => FilterLanguages();
        _language.QuerySubmitted += (_, args) =>
        {
            if (args.ChosenSuggestion is string name)
                _language.Text = name;
        };
        _languages.SelectionChanged += OnLanguageChoice;
        _information.Click += (_, _) =>
        {
            if (_model?.Supplier is { } supplier)
                Disclosure
                    .Create(
                        Text,
                        supplier.SupplierId.ToString(),
                        Text.Format("subtitles", "disclosure", supplier.SupplierId),
                        supplier.Privacy,
                        supplier.Terms
                    )
                    .ShowAt(_information);
        };
        Unloaded += (_, _) =>
        {
            _motion.Stop();
            SetVisible(false);
            Attach(null);
        };
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
        _automatic.Attach(_visible ? model : null);
        if (_visible && _model is not null)
            _model.Changed += OnChanged;
        Refresh();
    }

    internal void SetVisible(bool visible)
    {
        if (_visible == visible)
            return;
        _visible = visible;
        if (_model is not null)
        {
            if (visible)
                _model.Changed += OnChanged;
            else
                _model.Changed -= OnChanged;
        }
        _batch.SetVisible(visible && _tasks.SelectedItem == _searchTab);
        _automatic.Attach(visible ? _model : null);
        if (!visible)
            _motion.Stop();
        if (visible)
            RefreshText();
    }

    private void OnChanged(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(Refresh);

    private void OnTask(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is not { } selected)
            return;
        FrameworkElement content = selected == _searchTab ? _batch
            : selected == _filesTab ? _scanRow
            : _preferences;
        var previous = _motion.Current is { } current ? _pages.Children.IndexOf(current) : -1;
        var forward = _pages.Children.IndexOf(content) >= previous;
        _batch.SetVisible(_visible && selected == _searchTab);
        _motion.Show(content, forward);
    }

    private string T(string key) => Text.Get("subtitles", key);

    private string F(string key, params object[] values) => Text.Format("subtitles", key, values);

    internal void RefreshText()
    {
        _section.Header = Text.Get("plugins", "subtitles");
        _preferencesTab.Text = T("preferences");
        _searchTab.Text = T("library_search");
        _filesTab.Text = T("existing_files");
        AutomationProperties.SetName(_tasks, _section.Header);
        _automatic.Refresh();
        foreach (ComboBoxItem item in _extras.Items)
            item.Content = (int)item.Tag switch
            {
                0 => T("extras_none"),
                1 => T("extras_one"),
                var count => F("extras_count", count),
            };
        _extrasRow.Header = T("extras");
        _extrasRow.Description = T("extras_hint");
        _supplierRow.Header = T("supplier");
        _accessRow.Header = T("access");
        _accessRow.Description = T("access_hint");
        _languageRow.Header = T("language_selection");
        _languageRow.Description = T("languages_hint");
        AutomationProperties.SetName(_wanted, T("language_selection"));
        AutomationProperties.SetName(_languageMode, T("language_selection"));
        AutomationProperties.SetName(_languages, T("language_selection"));
        _languageCustom.Content = T("choose_languages");
        _scanRow.Header = T("scan_folders");
        _scanRow.Description = T("scan_short_hint");
        _scan.Text = T("scan");
        ToolTipService.SetToolTip(_scan, T("scan_hint"));
        _batch.Refresh();
        _check.Text = T("check");
        _edit.Text = T("edit");
        ToolTipService.SetToolTip(_check, T("check_hint"));
        ToolTipService.SetToolTip(_edit, T("edit_hint"));
        AutomationProperties.SetName(_language, T("languages"));
        AutomationProperties.SetName(_extras, T("extras"));
        AutomationProperties.SetHelpText(_extras, T("extras_hint"));
        AutomationProperties.SetName(_supplier, T("supplier"));
        ToolTipService.SetToolTip(_supplier, T("supplier_hint"));
        AutomationProperties.SetName(_information, T("supplier_information"));
        ToolTipService.SetToolTip(_information, T("supplier_information"));
        _language.Header = T("language_hint");
        _language.PlaceholderText = T("language_search");
        Refresh();
    }

    private void Refresh()
    {
        if (!_visible)
            return;
        _refreshing = true;
        if (!Saving)
            _extras.SelectedItem = _extras
                .Items.Cast<ComboBoxItem>()
                .FirstOrDefault(item => (int)item.Tag == _model?.Extras);
        _extras.IsEnabled = _model is not null;
        _scan.IsEnabled = _model is not null;
        var scanned =
            _scanning ? T("scanning")
            : _scanned is { } result ? F("scanned", result.Found, result.Unreadable)
            : string.Empty;
        _scanStatus.Refresh(
            _scanning,
            _scanned is { } scan ? scan.Unreadable == 0 : null,
            scanned,
            scanned
        );
        _check.IsEnabled = !Saving && _model?.CanCheck == true;
        _edit.IsEnabled = !Saving && _model is not null && !_checking;
        _language.IsEnabled = _languageMode.IsEnabled = _model is not null;
        _supplier.ItemsSource ??= _model
            ?.Suppliers.Select(supplier => supplier.SupplierId)
            .ToArray();
        if (!Saving)
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
        RefreshLanguages();
        _section.Summary = _model is { } model
            ? F(
                "preferences_summary",
                model.SupplierId,
                _selection.Text,
                Text.Get("settings", model.Enabled ? "on" : "off")
            )
            : "—";
        _refreshing = false;
    }

    private void RefreshLanguages()
    {
        var selected = _model?.EffectiveLanguages ?? [];
        _languageDefault.Content = Text.LanguageName(Text.Language);
        if (!Saving)
            _languageMode.SelectedIndex = _model?.Languages.Length > 0 ? 1 : 0;
        _languageChoices.Visibility =
            _languageMode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        _selection.Text = string.Join(", ", selected.Select(Text.LanguageName));
        ToolTipService.SetToolTip(_wanted, _selection.Text);
        _selected.Text = F("selected", selected.Count);
        var tags = (_model?.Supplier?.Languages ?? [])
            .Concat(selected)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(Text.LanguageName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var signature = string.Join(",", tags) + ":" + Text.Language + ":" + _model?.SupplierId;
        if (!Equals(_languages.Tag, signature))
        {
            _languages.Tag = signature;
            _languages.Items.Clear();
            foreach (var tag in tags)
            {
                var name = Text.LanguageName(tag);
                if (_model?.Supplier?.Supports(tag) != true)
                    name += " · " + F("unsupported", _model?.SupplierId ?? default);
                var choice = new ListViewItem
                {
                    Content = new TextBlock
                    {
                        Text = name,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                    Tag = tag,
                };
                AutomationProperties.SetAutomationId(choice, "SubtitleLanguage-" + tag);
                AutomationProperties.SetName(choice, name);
                ToolTipService.SetToolTip(choice, name);
                _languages.Items.Add(choice);
            }
        }
        foreach (var choice in _languages.Items.Cast<ListViewItem>())
        {
            var tag = (string)choice.Tag;
            if (!Saving)
            {
                if (selected.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    if (!_languages.SelectedItems.Contains(choice))
                        _languages.SelectedItems.Add(choice);
                }
                else
                    _languages.SelectedItems.Remove(choice);
            }
            choice.IsEnabled =
                _model is not null
                && (
                    _model.Supplier?.Supports(tag) == true
                    || selected.Contains(tag, StringComparer.OrdinalIgnoreCase)
                );
        }
        FilterLanguages();
    }

    private void FilterLanguages()
    {
        foreach (var choice in _languages.Items.Cast<ListViewItem>())
            choice.Visibility =
                ((TextBlock)choice.Content).Text.Contains(
                    _language.Text.Trim(),
                    StringComparison.CurrentCultureIgnoreCase
                )
                || ((string)choice.Tag).StartsWith(
                    _language.Text.Trim(),
                    StringComparison.OrdinalIgnoreCase
                )
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        _language.ItemsSource = _languages
            .Items.Cast<ListViewItem>()
            .Where(choice => choice.Visibility == Visibility.Visible)
            .Select(choice => ((TextBlock)choice.Content).Text)
            .ToArray();
        AlignLanguages();
    }

    private void ResizeLanguages(object sender, SizeChangedEventArgs args) => AlignLanguages();

    private void AlignLanguages()
    {
        if (_languages.ItemsPanelRoot is not { ActualWidth: > 0 } panel)
            return;
        var columns = panel.ActualWidth < 480 ? 1 : 2;
        var width = Math.Floor(panel.ActualWidth / columns);
        foreach (var choice in _languages.Items.Cast<ListViewItem>())
            choice.Width = width;
    }

    private async void OnLanguageChoice(object sender, SelectionChangedEventArgs args)
    {
        if (_refreshing || _model is not { } model)
            return;
        var added = args
            .AddedItems.Cast<ListViewItem>()
            .Select(choice => (string)choice.Tag)
            .ToArray();
        var removed = args
            .RemovedItems.Cast<ListViewItem>()
            .Select(choice => (string)choice.Tag)
            .ToArray();
        // Save refreshes afterwards, which checks a refused language again.
        await Save(model.ChangeLanguages(added, removed));
    }

    // Waits for a write the model queued, and reports its failure.
    private async Task Save(Task saving)
    {
        var model = _model;
        try
        {
            Refresh();
            await saving;
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("Subtitle settings save cancelled.");
        }
        catch (Exception error)
        {
            if (CanReport(model))
                _report(error, "failed_settings");
        }
        finally
        {
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
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Subtitle supplier check cancelled ({model.SupplierId})."
            );
        }
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
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("Subtitle file rescan cancelled.");
        }
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
            MaxWidth = 520,
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

    internal string Coverage
    {
        set => _batch.Coverage = value;
    }
}
