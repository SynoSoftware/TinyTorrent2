using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Engine;
using Syno.TinyTorrent.Shell;

namespace Syno.TinyTorrent.Settings;

public sealed record RemovalRow(string Name, string Reason)
{
    public int NameSpan => Reason.Length == 0 ? 2 : 1;
}

public sealed partial class IndexingTask : UserControl, IDraft, IDisposable
{
    private sealed record TextInput(TextBox Value, ComboBox Mode);
    private sealed record RangeInput(ComboBox Comparison, TextBox Value, TextBox End, ComboBox? Unit);

    private readonly CatalogueClient _client;
    private readonly IndexingPage _opening;
    private readonly List<Action> _text = [];
    private IReadOnlyList<Catalogue.Rule> _rules = [];
    private IReadOnlyList<Catalogue.Rule> _proposed = [];
    private Catalogue.Rule? _editing;
    private string _draftId = string.Empty;
    private Catalogue.PolicyPreview? _policy;
    private Catalogue.Removal? _removal;
    private IReadOnlyList<Catalogue.RemovalRow> _removalRows = [];
    private readonly List<long> _pages = [0];
    private int _pageIndex;
    private long _nextRow;
    private bool _moreRows;
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
    private bool _validRule;

    public MainViewModel Model { get; }
    internal Dialog Dialog { get; }
    public bool IsPending => _pending;
    public bool HasDraft => true;
    public bool CanSubmit => !_pending && !_stopped && Model.CanEdit
        && (_page == IndexingPage.Edit && _validRule || _page == IndexingPage.Policy && _policy is not null
            || _page == IndexingPage.Removal && _rowsReady && _removal?.Torrents > 0);
    public ClosePolicy ClosePolicy => new ClosePolicy.Discard();
    public event PropertyChangedEventHandler? PropertyChanged;

    internal IndexingTask(MainViewModel model, IndexingPage page, Catalogue.Rule? rule = null, bool clear = false)
    {
        Model = model;
        _client = model.Catalogue;
        _opening = _page = page;
        _editing = rule;
        _clearing = clear;
        InitializeComponent();
        Dialog = new Dialog { Content = this, Glyph = Lucide.ListFilter };
        Dialog.SizeToContent();
        Dialog.Opened += OnOpened;
        Dialog.CloseButtonClick += (_, args) =>
        {
            if (_pending) { _cancel = true; args.Cancel = true; }
        };
        _title = AddText(TorrentConditions, "title_condition", "RuleTitle");
        _size = AddRange(TorrentConditions, "total_size", "RuleSize", true);
        _count = AddRange(TorrentConditions, "file_count", "RuleCount", false);
        _filename = AddText(FileConditions, "filename", "RuleFilename");
        _path = AddText(FileConditions, "path", "RulePath");
        _extension = AddText(FileConditions, "extensions", "RuleExtensions", true);
        _fileSize = AddRange(FileConditions, "file_size", "RuleFileSize", true);
        Quantifier.ItemsSource = new[] { T("any_file"), T("every_file"), T("no_file") };
        Quantifier.SelectedIndex = 0;
        Quantifier.SelectionChanged += (_, _) => Validate();
        Model.PropertyChanged += OnModel;
        _loading = false;
        RefreshText();
    }

    private string T(string key) => Model.Text.Get("catalogue", key);

    private async void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args) =>
        await Run(async () =>
        {
            Width = Math.Min(_page == IndexingPage.Edit ? 800 : 560, Math.Max(320, Dialog.XamlRoot.Size.Width - 96));
            _rules = await _client.RulesAsync();
            if (_stopped)
                return;
            if (_opening == IndexingPage.Edit)
                Edit(_editing);
            else if (_opening == IndexingPage.Policy)
            {
                _proposed = _rules.Where(item => item.RuleId != _editing?.RuleId).ToArray();
                _policy = await _client.PreviewRulesAsync(_proposed);
                if (!_stopped)
                    SetPage(IndexingPage.Policy);
            }
            else await LoadRemoval(_clearing);
        });

    private void OnModel(object? sender, PropertyChangedEventArgs args) => Changed();


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
        Dialog.Glyph = _page == IndexingPage.Removal && _clearing ? Lucide.Trash2 : Lucide.ListFilter;
        Dialog.PrimaryGlyph = _page switch
        {
            IndexingPage.Edit => Lucide.ListFilter,
            IndexingPage.Removal => Lucide.Trash2,
            _ => Lucide.Check,
        };
        Dialog.CloseButtonText = Model.Text.Get("dialog", "cancel");
        AutomationProperties.SetName(Quantifier, T("file_matching"));
        _loading = true;
        RefreshChoices(Quantifier, [T("any_file"), T("every_file"), T("no_file")]);
        foreach (var refresh in _text)
            refresh();
        _loading = false;
        ShowPreview();
        Validate();
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

    internal static string Describe(Catalogue.Rule rule, Strings text)
    {
        string T(string key) => text.Get("catalogue", key);
        string RangeText(Catalogue.Range range, bool bytes)
        {
            string Value(long value) => bytes ? text.Bytes(value) : value.ToString("N0", CultureInfo.CurrentCulture);
            return range.Minimum == range.Maximum && range.Minimum is { } same ? "= " + Value(same)
                : range.Minimum is { } lower && range.Maximum is { } upper ? Value(lower) + " – " + Value(upper)
                : range.Minimum is { } min ? "≥ " + Value(min)
                : range.Maximum is { } max ? "≤ " + Value(max) : string.Empty;
        }
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

    private void SetPage(IndexingPage page)
    {
        _page = page;
        if (Dialog.XamlRoot is { } root)
            Width = Math.Min(page == IndexingPage.Edit ? 800 : 560, Math.Max(320, root.Size.Width - 96));
        EditorContent.Visibility = page == IndexingPage.Edit ? Visibility.Visible : Visibility.Collapsed;
        PreviewContent.Visibility = page is IndexingPage.Policy or IndexingPage.Removal ? Visibility.Visible : Visibility.Collapsed;
        Feedback.Visibility = Visibility.Collapsed;
        RefreshText();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_stopped || _page != page)
                return;
            Viewport.ChangeView(0, 0, null, true);
            if (page == IndexingPage.Edit)
                _title.Value.Focus(FocusState.Programmatic);
            else Dialog.Focus(FocusState.Programmatic);
        });
    }

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
        PolicyChanges.Children.Clear();
        RemovalList.Visibility = _page == IndexingPage.Removal && _removalRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Paging.Visibility = _page == IndexingPage.Removal && (_pageIndex > 0 || _moreRows) ? Visibility.Visible : Visibility.Collapsed;
        if (_page == IndexingPage.Policy && _policy is { } policy)
        {
            foreach (var rule in _proposed.Where(rule => !_rules.Contains(rule)))
                PolicyChange(_rules.Any(previous => previous.RuleId == rule.RuleId) ? "edit_rule" : "add_rule", rule);
            foreach (var rule in _rules.Where(rule => !_proposed.Any(next => next.RuleId == rule.RuleId)))
                PolicyChange("remove", rule);
            PreviewDescription.Text = T("policy_explanation");
            Fact("suppressed_now", policy.Suppressed.ToString("N0", CultureInfo.CurrentCulture));
            Fact("suppressed_after", policy.Retained.ToString("N0", CultureInfo.CurrentCulture));
            Fact("released", policy.Released.ToString("N0", CultureInfo.CurrentCulture));
        }
        else if (_page == IndexingPage.Removal && _removal is { } removal)
        {
            PreviewDescription.Text = removal.Torrents == 0 ? T(_clearing ? "empty" : "nothing_to_remove")
                : T(_clearing ? "clear_explanation" : "removal_explanation");
            if (removal.Torrents > 0)
            {
                PreviewFacts.Children.Add(new TextBlock
                {
                    Text = Model.Text.Format("catalogue", "removal_summary", removal.Torrents, removal.Files),
                    Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                });
                PreviewFacts.Children.Add(new TextBlock
                {
                    Text = Model.Text.Format("catalogue", "removal_size", Model.Text.Bytes(removal.Bytes)),
                    Style = (Style)Application.Current.Resources["TinyTorrentTitleDetailTextStyle"],
                });
            }
            RemovalEntries.ItemsSource = _removalRows.Select(row => new RemovalRow(
                row.Torrent.Name,
                _clearing ? string.Empty : string.Join(" · ", row.Reasons.Select(id =>
                    _rules.FirstOrDefault(rule => rule.RuleId == id) is { } rule ? Describe(rule, Model.Text) : T("rules")))
            )).ToArray();
            PageRange.Text = Model.Text.Format("catalogue", "removal_scope",
                _pageIndex + 1, removal.Torrents);
            Previous.IsEnabled = _pageIndex > 0;
            Next.IsEnabled = _moreRows;
        }
    }

    private void PolicyChange(string action, Catalogue.Rule rule) => PolicyChanges.Children.Add(new TextBlock
    {
        Text = T(action) + ": " + Describe(rule, Model.Text),
        TextWrapping = TextWrapping.Wrap,
    });

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
        _removalRows = [];
        _pages.Clear();
        _pages.Add(0);
        _pageIndex = 0;
        _rowsReady = false;
        SetPage(IndexingPage.Removal);
        await ReadRows(0);
    }

    private async Task ReadRows(int index)
    {
        if (_removal is not { } removal)
            return;
        var page = await _client.RemovalRowsAsync(removal.Token, _pages[index]);
        if (_stopped || _page != IndexingPage.Removal || _removal?.Token != removal.Token)
            return;
        _removalRows = page.Entries;
        _pageIndex = index;
        _nextRow = page.Next;
        _moreRows = page.More;
        _rowsReady = true;
        ShowPreview();
    }

    private async void OnPrevious(object sender, RoutedEventArgs args) => await Run(async () =>
    {
        if (_pageIndex > 0)
        {
            await ReadRows(_pageIndex - 1);
        }
    });

    private async void OnNext(object sender, RoutedEventArgs args) => await Run(async () =>
    {
        if (_moreRows)
        {
            if (_pages.Count == _pageIndex + 1)
                _pages.Add(_nextRow);
            await ReadRows(_pageIndex + 1);
        }
    });

    public async Task<bool> Submit()
    {
        if (!CanSubmit)
            return false;
        var completed = false;
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
                await Model.Search.RefreshStatus();
                completed = !_stopped;
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
                await Model.Search.RefreshStatus();
                completed = !_stopped;
            });
        return completed;
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
        Model.PropertyChanged -= OnModel;
    }

    private TextInput AddText(StackPanel panel, string key, string identifier, bool extensions = false)
    {
        var input = new TextBox();
        AutomationProperties.SetAutomationId(input, identifier);
        AutomationProperties.SetName(input, T(key));
        var mode = new ComboBox { ItemsSource = new[] { T(extensions ? "is_one_of" : "matches"), T(extensions ? "is_not_one_of" : "not_matches") }, SelectedIndex = 0 };
        AutomationProperties.SetName(mode, T(key) + " · " + T("comparison"));
        var label = new TextBlock { Text = T(key), VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(ConditionRow(label, mode, input));
        input.TextChanged += (_, _) => Validate();
        mode.SelectionChanged += (_, _) => Validate();
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
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var value = new TextBox { MinWidth = 72 };
        var end = new TextBox { MinWidth = 72 };
        var unit = bytes ? new ComboBox { ItemsSource = new[] { T("bytes"), T("unit_kib"), T("unit_mib"), T("unit_gib") }, SelectedIndex = 2 } : null;
        AutomationProperties.SetAutomationId(value, identifier);
        AutomationProperties.SetName(value, T(key));
        AutomationProperties.SetName(end, T(key) + " · " + T("upper"));
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var upper = new ColumnDefinition { Width = GridLength.Auto };
        row.ColumnDefinitions.Add(upper);
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
            row.Visibility = comparison.SelectedIndex != 0 ? Visibility.Visible : Visibility.Collapsed;
            end.Visibility = comparison.SelectedIndex == 6 ? Visibility.Visible : Visibility.Collapsed;
            upper.Width = comparison.SelectedIndex == 6 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        };
        row.Visibility = Visibility.Collapsed;
        end.Visibility = Visibility.Collapsed;
        var label = new TextBlock { Text = T(key), VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(ConditionRow(label, comparison, row));
        value.TextChanged += (_, _) => Validate();
        end.TextChanged += (_, _) => Validate();
        comparison.SelectionChanged += (_, _) => Validate();
        if (unit is not null)
            unit.SelectionChanged += (_, _) => Validate();
        _text.Add(() =>
        {
            label.Text = T(key);
            AutomationProperties.SetName(comparison, T(key) + " · " + T("comparison"));
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

    private static Grid ConditionRow(TextBlock label, Control comparison, FrameworkElement value)
    {
        var row = new Grid { ColumnSpacing = 12, RowSpacing = 8 };
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(124) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.SetBinding(ToolTipService.ToolTipProperty, new Microsoft.UI.Xaml.Data.Binding { Source = label, Path = new PropertyPath(nameof(TextBlock.Text)) });
        comparison.MinWidth = 144;
        row.Children.Add(label);
        row.Children.Add(comparison);
        row.Children.Add(value);
        void Arrange(double width)
        {
            var narrow = width < 620;
            row.ColumnDefinitions[0].Width = narrow ? GridLength.Auto : new GridLength(124);
            Grid.SetColumnSpan(label, narrow ? 3 : 1);
            Grid.SetRow(comparison, narrow ? 1 : 0);
            Grid.SetColumn(comparison, narrow ? 0 : 1);
            Grid.SetRow(value, narrow ? 1 : 0);
            Grid.SetColumn(value, narrow ? 1 : 2);
            Grid.SetColumnSpan(value, narrow ? 2 : 1);
        }
        row.SizeChanged += (_, args) => Arrange(args.NewSize.Width);
        Arrange(800);
        return row;
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

    private void Validate()
    {
        if (_loading || _page != IndexingPage.Edit)
            return;
        try
        {
            ReadRule();
            _validRule = true;
            Validation.Text = string.Empty;
        }
        catch (ArgumentException error)
        {
            _validRule = false;
            Validation.Text = error.Message;
        }
        Changed();
    }

    private Catalogue.Text ReadExtensions()
    {
        var values = _extension.Value.Text.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.TrimStart('.').ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order().ToArray();
        if (values.Any(value => value.Length == 0 || value.IndexOfAny(['*', '?', '/', '\\']) >= 0))
            throw new ArgumentException(T("invalid_extensions"));
        return new() { Value = string.Join(",", values), Exclude = _extension.Mode.SelectedIndex == 1 };
    }

}
