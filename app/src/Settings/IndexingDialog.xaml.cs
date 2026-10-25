using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Engine;
using Syno.TinyTorrent.Shell;
using Windows.System;

namespace Syno.TinyTorrent.Settings;

public sealed record RemovalRow(string Hash, string Name, string Reason);

public sealed partial class IndexingDialog : UserControl, IDraft, IDisposable
{
    private sealed record TextInput(TextBox Value, ComboBox Mode);
    private sealed record RangeInput(ComboBox Comparison, TextBox Value, TextBox End, ComboBox? Unit);

    private readonly CatalogueClient _client;
    private readonly bool _startsClear;
    private readonly List<Action> _text = [];
    private IReadOnlyList<Catalogue.Rule> _rules = [];
    private IReadOnlyList<Catalogue.Rule> _proposed = [];
    private Catalogue.Rule? _editing;
    private string _draftId = string.Empty;
    private Catalogue.PolicyPreview? _policy;
    private Catalogue.Removal? _removal;
    private readonly List<Catalogue.RemovalRow> _removalRows = [];
    private bool _rowsReady;
    private bool _clearing;
    private readonly TextInput _title;
    private readonly TextInput _filename;
    private readonly TextInput _path;
    private readonly TextInput _extension;
    private readonly RangeInput _size;
    private readonly RangeInput _count;
    private readonly RangeInput _fileSize;
    private IndexingPage _page;
    private bool _pending;
    private bool _stopped;
    private bool _cancel;
    private bool _loading = true;
    private int _budgetScale = 1;
    private int _staleScale = 24;

    public MainViewModel Model { get; }
    internal Dialog Dialog { get; }
    public bool IsPending => _pending;
    public bool HasDraft => _page != IndexingPage.Settings;
    public bool CanSubmit => !_pending && !_stopped && Model.CanEdit
        && (_page == IndexingPage.Edit && IsValidRule() || _page == IndexingPage.Policy
            || _page == IndexingPage.Removal && _rowsReady && _removal?.Torrents > 0);
    public ClosePolicy ClosePolicy => new ClosePolicy.Discard();
    public event PropertyChangedEventHandler? PropertyChanged;

    internal IndexingDialog(MainViewModel model, bool clear = false)
    {
        Model = model;
        _client = model.Catalogue;
        _startsClear = clear;
        InitializeComponent();
        Dialog = new Dialog { Content = this, Glyph = Lucide.Search };
        Dialog.Opened += OnOpened;
        Dialog.CloseButtonClick += OnClose;
        _title = AddText(TorrentConditions, "title_condition", "RuleTitle");
        _size = AddRange(TorrentConditions, "total_size", "RuleSize", true);
        _count = AddRange(TorrentConditions, "file_count", "RuleCount", false);
        _filename = AddText(FileConditions, "filename", "RuleFilename");
        _path = AddText(FileConditions, "path", "RulePath");
        _extension = AddText(FileConditions, "extensions", "RuleExtensions", true);
        _fileSize = AddRange(FileConditions, "file_size", "RuleFileSize", true);
        Quantifier.ItemsSource = new[] { T("any_file"), T("every_file"), T("no_file") };
        Quantifier.SelectedIndex = 0;
        StaleUnit.ItemsSource = new[] { T("hours"), T("days"), T("weeks") };
        StaleUnit.SelectedIndex = 1;
        BudgetUnit.ItemsSource = new[] { T("unit_mib"), T("unit_gib") };
        BudgetUnit.SelectedIndex = 0;
        RemovalEntries.Schema<RemovalRow>().Key(row => row.Hash);
        Model.Settings.PropertyChanged += OnSettings;
        Model.PropertyChanged += OnModel;
        Model.Search.PropertyChanged += OnSearch;
        _loading = false;
        RefreshInputs();
        RefreshText();
    }

    private string T(string key) => Model.Text.Get("catalogue", key);

    private async void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args) =>
        await Run(async () =>
        {
            _rules = await _client.RulesAsync();
            await RefreshStatus();
            if (_startsClear && !_stopped)
                await LoadRemoval(true);
        });

    private void OnSettings(object? sender, PropertyChangedEventArgs args)
    {
        if (_page == IndexingPage.Settings)
            RefreshInputs();
        Bindings.Update();
    }

    private void OnModel(object? sender, PropertyChangedEventArgs args) => Changed();

    private void OnSearch(object? sender, PropertyChangedEventArgs args) => ShowStatus();

    private void RefreshInputs()
    {
        var focused = XamlRoot is { } root ? FocusManager.GetFocusedElement(root) : null;
        if (!ReferenceEquals(focused, Budget) && !Model.Settings.IndexBudget.HasDraft && !Model.Settings.IndexBudget.IsPending)
            Budget.Text = (Model.Settings.IndexBudget.ConfirmedNumber / _budgetScale)
                .ToString("G", CultureInfo.CurrentCulture);
        if (!ReferenceEquals(focused, Stale) && !Model.Settings.IndexStale.HasDraft && !Model.Settings.IndexStale.IsPending)
            Stale.Text = (Model.Settings.IndexStale.ConfirmedNumber / _staleScale)
                .ToString("G", CultureInfo.CurrentCulture);
    }

    private async Task RefreshStatus()
    {
        await Model.Search.RefreshStatus();
        if (_stopped)
            return;
        ShowStatus();
        ShowRules();
    }

    private void ShowStatus()
    {
        var status = Model.Search.State;
        Usage.Text = Model.Text.Format("catalogue", "used", Model.Text.Bytes(status.Allocated));
        Excluded.Text = Model.Text.Format("catalogue", "excluded_count", status.Rejected);
    }

    internal void RefreshText()
    {
        Bindings.Update();
        Dialog.Title = T(_page switch
        {
            IndexingPage.Edit => _editing is null ? "add_rule" : "edit_rule",
            IndexingPage.Policy => "apply_rules",
            IndexingPage.Removal => _clearing ? "clear_title" : "remove_entries",
            _ => "settings_title",
        });
        Dialog.PrimaryButtonText = _page switch
        {
            IndexingPage.Edit => T("review"),
            IndexingPage.Policy => T("apply"),
            IndexingPage.Removal => T(_clearing ? "delete" : "remove"),
            _ => string.Empty,
        };
        Dialog.PrimaryGlyph = _page == IndexingPage.Edit ? Lucide.Search : Lucide.Check;
        Dialog.CloseButtonText = _page == IndexingPage.Settings ? T("done") : Model.Text.Get("dialog", "cancel");
        AutomationProperties.SetName(Budget, T("budget"));
        AutomationProperties.SetName(BudgetUnit, T("budget_unit"));
        AutomationProperties.SetName(Stale, T("stale"));
        AutomationProperties.SetName(StaleUnit, T("age_unit"));
        AutomationProperties.SetName(Quantifier, T("file_matching"));
        _loading = true;
        RefreshChoices(Quantifier, [T("any_file"), T("every_file"), T("no_file")]);
        RefreshChoices(StaleUnit, [T("hours"), T("days"), T("weeks")]);
        RefreshChoices(BudgetUnit, [T("unit_mib"), T("unit_gib")]);
        foreach (var refresh in _text)
            refresh();
        _loading = false;
        ShowStatus();
        ShowRules();
        RemovalEntries.Strings = Model.Text.Table;
        TorrentColumn.DisplayName = T("torrent");
        ReasonColumn.DisplayName = T("reason");
        ShowPreview();
        Changed();
    }

    private void Changed()
    {
        IsEnabled = !_pending && Model.CanEdit;
        PropertyChanged?.Invoke(this, new(string.Empty));
    }

    private static void RefreshChoices(ComboBox control, string[] choices)
    {
        var selected = control.SelectedIndex;
        control.ItemsSource = choices;
        control.SelectedIndex = selected;
    }

    private void ShowRules()
    {
        RulesContent.Children.Clear();
        if (_rules.Count == 0)
            RulesContent.Children.Add(new TextBlock { Text = T("no_rules") });
        foreach (var rule in _rules)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock
            {
                Text = Describe(rule),
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(label, label.Text);
            AutomationProperties.SetHelpText(label, label.Text);
            row.Children.Add(label);
            var edit = new ActionButton { Text = T("edit"), Glyph = Lucide.Pencil };
            edit.Click += (_, _) => Edit(rule);
            Grid.SetColumn(edit, 1);
            row.Children.Add(edit);
            var remove = new ActionButton { Text = T("remove"), Glyph = Lucide.Trash2 };
            remove.Click += async (_, _) => await PreviewRules(_rules.Where(item => item.RuleId != rule.RuleId).ToArray());
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);
            RulesContent.Children.Add(row);
        }
    }

    private string Describe(Catalogue.Rule rule)
    {
        var facts = new List<string>();
        if (rule.Title.Value.Length > 0)
            facts.Add(T("title_condition") + ": " + (rule.Title.Exclude ? T("not") + " " : string.Empty) + rule.Title.Value);
        if (rule.Size.Minimum is not null || rule.Size.Maximum is not null)
            facts.Add(T("total_size") + ": " + RangeText(rule.Size, true));
        if (rule.Count.Minimum is not null || rule.Count.Maximum is not null)
            facts.Add(T("file_count") + ": " + RangeText(rule.Count, false));
        var files = new List<string>();
        foreach (var (key, condition) in new[] { ("filename", rule.Filename), ("path", rule.Path), ("extensions", rule.Extension) })
            if (condition.Value.Length > 0)
                files.Add(T(key) + ": " + (condition.Exclude ? T("not") + " " : string.Empty) + condition.Value);
        if (rule.FileSize.Minimum is not null || rule.FileSize.Maximum is not null)
            files.Add(T("file_size") + ": " + RangeText(rule.FileSize, true));
        if (files.Count > 0)
            facts.Add(T(rule.Quantifier switch
            {
                FileQuantifier.Every => "every_file",
                FileQuantifier.None => "no_file",
                _ => "any_file",
            }) + " · " + string.Join("; ", files));
        return string.Join(" · ", facts);
    }

    private string RangeText(Catalogue.Range range, bool bytes)
    {
        string Value(long value) => bytes ? Model.Text.Bytes(value) : value.ToString("N0", CultureInfo.CurrentCulture);
        return range.Minimum == range.Maximum && range.Minimum is { } same ? "= " + Value(same)
            : range.Minimum is { } lower && range.Maximum is { } upper ? Value(lower) + " – " + Value(upper)
            : range.Minimum is { } min ? "≥ " + Value(min)
            : range.Maximum is { } max ? "≤ " + Value(max) : string.Empty;
    }

    private void SetPage(IndexingPage page)
    {
        _page = page;
        SettingsContent.Visibility = page == IndexingPage.Settings ? Visibility.Visible : Visibility.Collapsed;
        EditorContent.Visibility = page == IndexingPage.Edit ? Visibility.Visible : Visibility.Collapsed;
        PreviewContent.Visibility = page is IndexingPage.Policy or IndexingPage.Removal ? Visibility.Visible : Visibility.Collapsed;
        Feedback.Visibility = Visibility.Collapsed;
        RefreshText();
    }

    private void OnAdd(object sender, RoutedEventArgs args) => Edit(null);

    private void Edit(Catalogue.Rule? rule)
    {
        _editing = rule;
        _draftId = rule?.RuleId ?? Guid.NewGuid().ToString("N");
        Set(_title, rule?.Title);
        Set(_filename, rule?.Filename);
        Set(_path, rule?.Path);
        Set(_extension, rule?.Extension);
        Set(_size, rule?.Size);
        Set(_count, rule?.Count);
        Set(_fileSize, rule?.FileSize);
        Quantifier.SelectedIndex = (int)(rule?.Quantifier ?? FileQuantifier.Any);
        SetPage(IndexingPage.Edit);
    }

    private async Task PreviewRules(IReadOnlyList<Catalogue.Rule> proposed) => await Run(async () =>
    {
        _policy = await _client.PreviewRulesAsync(proposed);
        if (_stopped)
            return;
        _proposed = proposed;
        SetPage(IndexingPage.Policy);
    });

    private void ShowPreview()
    {
        PreviewFacts.Children.Clear();
        RemovalEntries.Visibility = _page == IndexingPage.Removal ? Visibility.Visible : Visibility.Collapsed;
        PreviewCoverage.Visibility = _page == IndexingPage.Removal ? Visibility.Visible : Visibility.Collapsed;
        if (_page == IndexingPage.Policy && _policy is { } policy)
        {
            PreviewDescription.Text = T("policy_explanation");
            Fact("suppressed_now", policy.Suppressed.ToString("N0", CultureInfo.CurrentCulture));
            Fact("suppressed_after", policy.Retained.ToString("N0", CultureInfo.CurrentCulture));
            Fact("released", policy.Released.ToString("N0", CultureInfo.CurrentCulture));
        }
        else if (_page == IndexingPage.Removal && _removal is { } removal)
        {
            PreviewDescription.Text = T(_clearing ? "clear_explanation" : "removal_explanation");
            Fact("torrents", removal.Torrents.ToString("N0", CultureInfo.CurrentCulture));
            Fact("files", removal.Files.ToString("N0", CultureInfo.CurrentCulture));
            Fact("retained_bytes", Model.Text.Bytes(removal.Bytes));
            RemovalEntries.ItemsSource = _removalRows.Select(row => new RemovalRow(
                row.Torrent.Hash, row.Torrent.Name,
                _clearing ? T("clear") : string.Join(" · ", row.Reasons.Select(id =>
                    _rules.FirstOrDefault(rule => rule.RuleId == id) is { } rule ? Describe(rule) : T("rules")))
            )).ToArray();
            PreviewCoverage.Text = Model.Text.Format("catalogue", "preview_coverage", _removalRows.Count, removal.Torrents);
        }
    }

    private void Fact(string key, string value)
    {
        var row = new Grid { ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new TextBlock
        {
            Text = T(key),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTipService.SetToolTip(label, label.Text);
        AutomationProperties.SetHelpText(label, label.Text);
        row.Children.Add(label);
        var content = new TextBlock { Text = value, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
        Grid.SetColumn(content, 1);
        row.Children.Add(content);
        PreviewFacts.Children.Add(row);
    }

    private async void OnReview(object sender, RoutedEventArgs args) => await PreviewRemoval(false);
    private async void OnClear(object sender, RoutedEventArgs args) => await PreviewRemoval(true);
    private Task PreviewRemoval(bool clear) => Run(() => LoadRemoval(clear));
    private async Task LoadRemoval(bool clear)
    {
        _cancel = false;
        do
        {
            _removal = await _client.PreviewRemovalAsync(clear);
        } while (_removal.More && !_cancel && !_stopped);
        if (_cancel || _stopped)
            return;
        _clearing = clear;
        _removalRows.Clear();
        _rowsReady = false;
        SetPage(IndexingPage.Removal);
        await ReadRows();
    }

    private async Task ReadRows()
    {
        if (_removal is not { } removal)
            return;
        var page = await _client.RemovalRowsAsync(removal.Token);
        if (_stopped || _page != IndexingPage.Removal || _removal?.Token != removal.Token)
            return;
        _removalRows.AddRange(page.Entries);
        _rowsReady = true;
        ShowPreview();
    }

    private async void OnRebuild(object sender, RoutedEventArgs args) => await Run(async () =>
    {
        await Model.Search.Rebuild();
        await RefreshStatus();
    });

    public async Task<bool> Submit()
    {
        if (!CanSubmit)
            return false;
        if (_page == IndexingPage.Edit)
        {
            try
            {
                var rule = ReadRule();
                await PreviewRules(_editing is null
                    ? [.. _rules, rule]
                    : _rules.Select(item => item.RuleId == _editing.RuleId ? rule : item).ToArray());
            }
            catch (ArgumentException error)
            {
                ShowError(error.Message);
            }
        }
        else if (_page == IndexingPage.Policy)
            await Run(async () =>
            {
                _rules = await _client.SaveRulesAsync(_proposed);
                await RefreshStatus();
                if (!_stopped)
                    SetPage(IndexingPage.Settings);
            });
        else if (_page == IndexingPage.Removal && _removal is { } removal)
            await Run(async () =>
            {
                _cancel = false;
                if (_clearing)
                {
                    await Model.Settings.Indexing.Submit(false);
                    if (Model.Settings.Indexing.Failure is { } failure)
                        throw failure;
                }
                if (_cancel || _stopped)
                    return;
                WorkObservation Progress(long removed) => new(
                    new WorkDescription.Maintenance(MaintenanceWork.CatalogueRemoval),
                    new WorkProgress.Fraction(Math.Clamp(removed, 0, removal.Torrents), removal.Torrents, WorkUnit.Percent),
                    new WorkExecution.Running());
                using (var operation = Model.Work.Begin(Progress(0), () =>
                {
                    _cancel = true;
                    return Task.CompletedTask;
                }))
                {
                    operation.ReportsSuccess = false;
                    try
                    {
                        Catalogue.Removal progress;
                        do
                        {
                            progress = await _client.RemoveAsync(removal.Token);
                            operation.Report(Progress(progress.Removed));
                        } while (progress.More && !_cancel && !_stopped);
                        operation.Outcome = progress.More ? WorkOutcome.Cancelled : WorkOutcome.Completed;
                    }
                    catch (Exception error)
                    {
                        operation.Error = error;
                        throw;
                    }
                }
                await RefreshStatus();
                if (!_stopped)
                    SetPage(IndexingPage.Settings);
            });
        return false;
    }

    private async void OnClose(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_pending)
        {
            _cancel = true;
            args.Cancel = true;
        }
        else if (_page != IndexingPage.Settings && !Model.IsClosing)
        {
            args.Cancel = true;
            SetPage(IndexingPage.Settings);
        }
        else
        {
            var deferral = args.GetDeferral();
            try
            {
                await CommitSetting(Model.Settings.IndexBudget, Budget, _budgetScale);
                await CommitSetting(Model.Settings.IndexStale, Stale, _staleScale);
            }
            finally { deferral.Complete(); }
        }
    }

    private async Task Run(Func<Task> action)
    {
        if (_pending || _stopped)
            return;
        _pending = true;
        Feedback.Visibility = Visibility.Collapsed;
        Changed();
        try { await action(); }
        catch (Exception error)
        {
            if (!_stopped)
                ShowError(Model.Text.Error(error));
        }
        finally
        {
            _pending = false;
            if (!_stopped)
                Changed();
        }
    }

    private void ShowError(string message)
    {
        Feedback.Text = message;
        Feedback.Visibility = Visibility.Visible;
    }

    public Task Discard()
    {
        _proposed = [];
        return Task.CompletedTask;
    }
    public void Stop() { _stopped = true; _cancel = true; }
    public void Dispose()
    {
        Stop();
        Model.Settings.PropertyChanged -= OnSettings;
        Model.PropertyChanged -= OnModel;
        Model.Search.PropertyChanged -= OnSearch;
    }

    private TextInput AddText(StackPanel panel, string key, string identifier, bool extensions = false)
    {
        var input = new TextBox();
        AutomationProperties.SetAutomationId(input, identifier);
        AutomationProperties.SetName(input, T(key));
        var mode = new ComboBox { ItemsSource = new[] { T(extensions ? "is_one_of" : "matches"), T(extensions ? "is_not_one_of" : "not_matches") }, SelectedIndex = 0 };
        AutomationProperties.SetName(mode, T(key) + " · " + T("comparison"));
        var row = new StackPanel { Spacing = 8 };
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new TextBlock { Text = T(key), VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(label);
        Grid.SetColumn(mode, 1);
        header.Children.Add(mode);
        row.Children.Add(header);
        row.Children.Add(input);
        panel.Children.Add(row);
        input.TextChanged += (_, _) => Changed();
        mode.SelectionChanged += (_, _) => Changed();
        _text.Add(() =>
        {
            label.Text = T(key);
            AutomationProperties.SetName(input, T(key));
            AutomationProperties.SetName(mode, T(key) + " · " + T("comparison"));
            RefreshChoices(mode, [T(extensions ? "is_one_of" : "matches"), T(extensions ? "is_not_one_of" : "not_matches")]);
        });
        return new(input, mode);
    }

    private RangeInput AddRange(StackPanel panel, string key, string identifier, bool bytes)
    {
        var comparison = new ComboBox
        {
            ItemsSource = new[] { T("any"), ">", "≥", "=", "<", "≤", T("between") },
            SelectedIndex = 0,
            Header = T(key),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var value = new TextBox();
        var end = new TextBox();
        var unit = bytes ? new ComboBox { ItemsSource = new[] { T("bytes"), T("unit_kib"), T("unit_mib"), T("unit_gib") }, SelectedIndex = 2 } : null;
        AutomationProperties.SetAutomationId(value, identifier);
        AutomationProperties.SetName(value, T(key));
        AutomationProperties.SetName(end, T(key) + " · " + T("upper"));
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(value);
        Grid.SetColumn(end, 1);
        row.Children.Add(end);
        if (unit is not null)
        {
            AutomationProperties.SetName(unit, T(key) + " · " + T("unit"));
            Grid.SetColumn(unit, 2);
            row.Children.Add(unit);
        }
        comparison.SelectionChanged += (_, _) =>
        {
            value.IsEnabled = comparison.SelectedIndex != 0;
            end.Visibility = comparison.SelectedIndex == 6 ? Visibility.Visible : Visibility.Collapsed;
        };
        value.IsEnabled = false;
        end.Visibility = Visibility.Collapsed;
        var fields = new StackPanel { Spacing = 8 };
        fields.Children.Add(comparison);
        fields.Children.Add(row);
        panel.Children.Add(fields);
        value.TextChanged += (_, _) => Changed();
        end.TextChanged += (_, _) => Changed();
        comparison.SelectionChanged += (_, _) => Changed();
        if (unit is not null)
            unit.SelectionChanged += (_, _) => Changed();
        _text.Add(() =>
        {
            comparison.Header = T(key);
            RefreshChoices(comparison, [T("any"), ">", "≥", "=", "<", "≤", T("between")]);
            AutomationProperties.SetName(value, T(key));
            AutomationProperties.SetName(end, T(key) + " · " + T("upper"));
            if (unit is not null)
            {
                RefreshChoices(unit, [T("bytes"), T("unit_kib"), T("unit_mib"), T("unit_gib")]);
                AutomationProperties.SetName(unit, T(key) + " · " + T("unit"));
            }
        });
        return new(comparison, value, end, unit);
    }

    private static void Set(TextInput input, Catalogue.Text? condition)
    {
        input.Value.Text = condition?.Value ?? string.Empty;
        input.Mode.SelectedIndex = condition?.Exclude == true ? 1 : 0;
    }

    private static void Set(RangeInput input, Catalogue.Range? condition)
    {
        var range = condition ?? new();
        if (input.Unit is not null)
            input.Unit.SelectedIndex = 0;
        input.Comparison.SelectedIndex = (range.Minimum, range.Maximum) switch
        {
            (null, null) => 0,
            ({ } lower, { } upper) when lower == upper => 3,
            ({ }, null) => 2,
            (null, { }) => 5,
            _ => 6,
        };
        input.Value.Text = (range.Minimum ?? range.Maximum)?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
        input.End.Text = range.Maximum?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
    }

    private static Catalogue.Text Read(TextInput input) => new()
    {
        Value = input.Value.Text.Trim(),
        Exclude = input.Mode.SelectedIndex == 1,
    };

    private Catalogue.Range Read(RangeInput input)
    {
        if (input.Comparison.SelectedIndex == 0)
            return new();
        var scale = input.Unit?.SelectedIndex switch { 1 => 1024L, 2 => 1048576L, 3 => 1073741824L, _ => 1L };
        long Value(TextBox box)
        {
            if (!decimal.TryParse(box.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var value)
                || value < 0 || value > long.MaxValue / (decimal)scale || value * scale != decimal.Truncate(value * scale))
                throw new ArgumentException(T("invalid_rule"));
            return (long)(value * scale);
        }
        var first = Value(input.Value);
        var second = input.Comparison.SelectedIndex == 6 ? Value(input.End) : first;
        if (second < first || input.Comparison.SelectedIndex == 1 && first == long.MaxValue
            || input.Comparison.SelectedIndex == 4 && first == 0)
            throw new ArgumentException(T("invalid_rule"));
        return input.Comparison.SelectedIndex switch
        {
            1 => new() { Minimum = first + 1 },
            2 => new() { Minimum = first },
            3 => new() { Minimum = first, Maximum = first },
            4 => new() { Maximum = first - 1 },
            5 => new() { Maximum = first },
            _ => new() { Minimum = first, Maximum = second },
        };
    }

    private Catalogue.Rule ReadRule()
    {
        var rule = new Catalogue.Rule
        {
            RuleId = _draftId,
            Name = _editing?.Name ?? string.Empty,
            Enabled = _editing?.Enabled ?? true,
            Title = Read(_title), Size = Read(_size), Count = Read(_count),
            Filename = Read(_filename), Path = Read(_path), Extension = ReadExtensions(),
            FileSize = Read(_fileSize), Quantifier = (FileQuantifier)Quantifier.SelectedIndex,
        };
        if (new[] { rule.Title, rule.Filename, rule.Path, rule.Extension }.All(value => value.Value.Length == 0)
            && new[] { rule.Size, rule.Count, rule.FileSize }.All(range => range.Minimum is null && range.Maximum is null))
            throw new ArgumentException(T("empty_rule"));
        return rule;
    }

    private bool IsValidRule()
    {
        try { ReadRule(); return true; }
        catch (ArgumentException) { return false; }
    }

    private Catalogue.Text ReadExtensions()
    {
        var values = _extension.Value.Text.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.TrimStart('.').ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order().ToArray();
        if (values.Any(value => value.Length == 0 || value.IndexOfAny(['*', '?', '/', '\\']) >= 0))
            throw new ArgumentException(T("invalid_extensions"));
        return new() { Value = string.Join(",", values), Exclude = _extension.Mode.SelectedIndex == 1 };
    }

    private async Task CommitSetting(Setting setting, TextBox box, int scale)
    {
        if (_loading || _stopped || !Model.CanEdit)
            return;
        if (decimal.TryParse(box.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var value)
            && value >= 1m / scale && value <= int.MaxValue / (decimal)scale)
        {
            var scaled = Math.Round(value * scale, 8);
            if (scaled == decimal.Truncate(scaled))
            {
                setting.Input = scaled.ToString("0", CultureInfo.CurrentCulture);
                await setting.Commit();
                return;
            }
        }
        ShowError(T("invalid_setting"));
    }

    private async void OnBudgetDeparture(object sender, RoutedEventArgs args) =>
        await CommitSetting(Model.Settings.IndexBudget, Budget, _budgetScale);
    private async void OnStaleDeparture(object sender, RoutedEventArgs args) =>
        await CommitSetting(Model.Settings.IndexStale, Stale, _staleScale);
    private async void OnBudgetKey(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter)
        {
            args.Handled = true;
            await CommitSetting(Model.Settings.IndexBudget, Budget, _budgetScale);
        }
    }
    private async void OnStaleKey(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter)
        {
            args.Handled = true;
            await CommitSetting(Model.Settings.IndexStale, Stale, _staleScale);
        }
    }
    private void OnBudgetUnit(object sender, SelectionChangedEventArgs args) =>
        ConvertUnit(Budget, ref _budgetScale, BudgetUnit.SelectedIndex == 1 ? 1024 : 1);
    private void OnStaleUnit(object sender, SelectionChangedEventArgs args) =>
        ConvertUnit(Stale, ref _staleScale, StaleUnit.SelectedIndex switch { 1 => 24, 2 => 168, _ => 1 });
    private void ConvertUnit(TextBox? box, ref int scale, int next)
    {
        if (box is not null && !_loading
            && decimal.TryParse(box.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var value)
            && value >= 0 && value <= decimal.MaxValue / scale)
            box.Text = (value * scale / next).ToString("G", CultureInfo.CurrentCulture);
        scale = next;
    }
}
