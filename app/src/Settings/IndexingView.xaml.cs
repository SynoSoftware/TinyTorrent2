using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Shared.Engine;
using Syno.TinyTorrent.Shell;
using Windows.System;

namespace Syno.TinyTorrent.Settings;

public sealed partial class IndexingView : UserControl
{
    private IReadOnlyList<Catalogue.Rule> _rules = [];
    private bool _visible;
    private bool _refreshing;
    private bool _reading;
    private bool _composing;
    private int _budgetScale = 1;
    private int _staleScale = 24;

    public MainViewModel Model { get; }
    internal event EventHandler<IndexingTask>? TaskRequested;
    internal event EventHandler? RebuildRequested;

    internal IndexingView(MainViewModel model)
    {
        Model = model;
        InitializeComponent();
        Budget.TextChanged += (_, _) => Stage(Model.Settings.IndexBudget, Budget, _budgetScale);
        Stale.TextChanged += (_, _) => Stage(Model.Settings.IndexStale, Stale, _staleScale);
        foreach (var field in new[] { Budget, Stale })
        {
            field.TextCompositionStarted += (_, _) => _composing = true;
            field.TextCompositionEnded += (_, _) => _composing = false;
        }
        RefreshText();
        RefreshInputs();
    }

    internal void SetVisible(bool visible)
    {
        if (_visible == visible)
            return;
        _visible = visible;
        if (visible)
        {
            RefreshInputs();
            Model.Settings.PropertyChanged += OnSettings;
            Model.Search.PropertyChanged += OnSearch;
            Model.PropertyChanged += OnModel;
            _ = Refresh();
        }
        else
        {
            Model.Settings.PropertyChanged -= OnSettings;
            Model.Search.PropertyChanged -= OnSearch;
            Model.PropertyChanged -= OnModel;
        }
    }

    private string T(string key) => Model.Text.Get("catalogue", key);
    private void OnSettings(object? sender, PropertyChangedEventArgs args)
    {
        RefreshInputs();
        ShowStatus();
    }
    private void OnSearch(object? sender, PropertyChangedEventArgs args) => ShowStatus();
    private void OnModel(object? sender, PropertyChangedEventArgs args) => IsEnabled = Model.CanEdit;

    internal async Task Refresh()
    {
        if (_reading)
            return;
        _reading = true;
        try
        {
            Feedback.Visibility = Visibility.Collapsed;
            _rules = await Model.Catalogue.RulesAsync();
            await Model.Search.RefreshStatus();
            ShowRules();
            ShowStatus();
        }
        catch (Exception error) { ShowError(Model.Text.Error(error)); }
        finally { _reading = false; }
    }

    internal void RefreshText()
    {
        Bindings.Update();
        _refreshing = true;
        BudgetUnit.ItemsSource = new[] { T("unit_mib"), T("unit_gib") };
        BudgetUnit.SelectedIndex = _budgetScale == 1024 ? 1 : 0;
        StaleUnit.ItemsSource = new[] { T("hours"), T("days"), T("weeks") };
        StaleUnit.SelectedIndex = _staleScale switch { 24 => 1, 168 => 2, _ => 0 };
        _refreshing = false;
        AutomationProperties.SetName(Budget, T("budget"));
        AutomationProperties.SetName(BudgetUnit, T("budget_unit"));
        AutomationProperties.SetName(Stale, T("stale"));
        AutomationProperties.SetName(StaleUnit, T("age_unit"));
        ShowStatus();
        ShowRules();
    }

    internal Control? Field(string name) => name switch
    {
        "indexing" => Enabled,
        "index_budget_mib" => Budget,
        "index_stale_hours" => Stale,
        _ => null,
    };

    private void RefreshInputs()
    {
        _refreshing = true;
        var focused = XamlRoot is { } root ? FocusManager.GetFocusedElement(root) : null;
        if (!ReferenceEquals(focused, Budget) && !Model.Settings.IndexBudget.HasDraft && !Model.Settings.IndexBudget.IsPending)
            Budget.Text = (Model.Settings.IndexBudget.ConfirmedNumber / _budgetScale).ToString("G", CultureInfo.CurrentCulture);
        if (!ReferenceEquals(focused, Stale) && !Model.Settings.IndexStale.HasDraft && !Model.Settings.IndexStale.IsPending)
            Stale.Text = (Model.Settings.IndexStale.ConfirmedNumber / _staleScale).ToString("G", CultureInfo.CurrentCulture);
        IsEnabled = Model.CanEdit;
        _refreshing = false;
    }

    private void ShowStatus()
    {
        var status = Model.Search.State;
        BudgetRow.Description = Model.Text.Format("catalogue", "used", Model.Text.Bytes(status.Allocated));
        Exclusions.Header = Model.Text.Format("catalogue", "excluded_count", status.Rejected);
        Indexed.Text = Model.Search.Totals;
        Rate.Text = Model.Search.Rate;
        RateGroup.Visibility = Model.Search.ShowsRate ? Visibility.Visible : Visibility.Collapsed;
        var collecting = status.Enabled && !Model.Search.CollectionUnavailable;
        Activity.Text = collecting && status.AcquiringLimit is > 0
            ? Model.Text.Format("catalogue", "fetching_limit", status.Acquiring, status.AcquiringLimit.Value)
            : Model.Search.Collection;
        Probes.Text = !collecting ? "—" : status.ProbingLimit is > 0
            ? Model.Text.Format("catalogue", "probing_limit", status.Probing, status.ProbingLimit.Value)
            : status.Probing.ToString("N0", CultureInfo.CurrentCulture);
        ToolTipService.SetToolTip(Indexed, Model.Search.StatusTip);
        ToolTipService.SetToolTip(Rate, Model.Text.Format("catalogue", "index_rate_hint", status.TorrentsPerSecond));
        ToolTipService.SetToolTip(Activity, Model.Search.StatusTip);
        AutomationProperties.SetName(Activity, collecting ? Activity.Text : Model.Search.StatusTip);
        CollectionSection.Summary = Model.Search.Totals + " · " + Model.Search.Collection;
        StorageSection.Summary = Model.Text.Format("catalogue", "storage_summary", Model.Text.Bytes(status.Allocated),
            Model.Text.Bytes(Model.Settings.IndexBudget.ConfirmedNumber * 1048576L),
            (Model.Settings.IndexStale.ConfirmedNumber / _staleScale).ToString("G", CultureInfo.CurrentCulture),
            _staleScale == 1 ? T("hours") : _staleScale == 168 ? T("weeks") : T("days"));
        RulesSection.Summary = Model.Text.Format("catalogue", "rules_summary", _rules.Count(rule => rule.Enabled), status.Rejected);
        MaintenanceSection.Summary = !Model.IsConnected || Model.Search.Condition != CatalogueCondition.None ? T("indexing_unavailable")
            : status.Rebuilding ? Model.Text.Get("work", "maintenance_rebuild") : status.Projecting ? T("updating_search")
            : !status.Exists ? T("no_catalogue") : T("search_ready");
    }

    private void ShowRules()
    {
        RulesContent.Children.Clear();
        if (_rules.Count == 0)
            RulesContent.Children.Add(new SettingsRow { Header = T("no_rules"), BorderThickness = new Thickness(0) });
        foreach (var rule in _rules)
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var edit = new ActionButton { Text = T("edit"), Glyph = Lucide.Pencil };
            edit.Click += (_, _) => Open(IndexingPage.Edit, rule);
            var remove = new ActionButton { Text = T("remove"), Glyph = Lucide.Trash2 };
            remove.Click += (_, _) => Open(IndexingPage.Policy, rule);
            actions.Children.Add(edit);
            actions.Children.Add(remove);
            RulesContent.Children.Add(new SettingsRow
            {
                Header = IndexingTask.Describe(rule, Model.Text),
                Content = actions,
            });
        }
    }

    private void Open(IndexingPage page, Catalogue.Rule? rule = null, bool clear = false) =>
        TaskRequested?.Invoke(this, new IndexingTask(Model, page, rule, clear));
    private void OnAdd(object sender, RoutedEventArgs args) => Open(IndexingPage.Edit);
    private void OnReview(object sender, RoutedEventArgs args) => Open(IndexingPage.Removal);
    private void OnClear(object sender, RoutedEventArgs args) => Open(IndexingPage.Removal, clear: true);
    private void OnRebuild(object sender, RoutedEventArgs args) => RebuildRequested?.Invoke(this, EventArgs.Empty);

    private void ShowError(string text)
    {
        Feedback.Text = text;
        Feedback.Visibility = Visibility.Visible;
    }

    private async Task Commit(Setting setting, TextBox box, int scale)
    {
        if (_refreshing || _composing || !Model.CanEdit)
            return;
        Stage(setting, box, scale);
        await setting.Commit();
    }

    private void Stage(Setting setting, TextBox box, int scale)
    {
        if (_refreshing || !Model.CanEdit)
            return;
        setting.Input = decimal.TryParse(box.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var value)
            && value >= decimal.MinValue / scale && value <= decimal.MaxValue / scale
            ? (value * scale).ToString("G29", CultureInfo.CurrentCulture) : box.Text;
    }

    private async void OnBudgetDeparture(object sender, RoutedEventArgs args) => await Commit(Model.Settings.IndexBudget, Budget, _budgetScale);
    private async void OnStaleDeparture(object sender, RoutedEventArgs args) => await Commit(Model.Settings.IndexStale, Stale, _staleScale);
    private async void OnBudgetKey(object sender, KeyRoutedEventArgs args) => await HandleKey(args, Model.Settings.IndexBudget, Budget, _budgetScale);
    private async void OnStaleKey(object sender, KeyRoutedEventArgs args) => await HandleKey(args, Model.Settings.IndexStale, Stale, _staleScale);
    private async Task HandleKey(KeyRoutedEventArgs args, Setting setting, TextBox box, int scale)
    {
        if (_composing)
            return;
        if (args.Key == VirtualKey.Enter)
        {
            args.Handled = true;
            await Commit(setting, box, scale);
        }
        else if (args.Key == VirtualKey.Escape)
        {
            args.Handled = true;
            setting.Cancel();
            box.Text = (setting.ConfirmedNumber / scale).ToString("G", CultureInfo.CurrentCulture);
            Feedback.Visibility = Visibility.Collapsed;
        }
    }

    private void OnBudgetUnit(object sender, SelectionChangedEventArgs args) => ConvertUnit(Model.Settings.IndexBudget, Budget, ref _budgetScale, BudgetUnit.SelectedIndex == 1 ? 1024 : 1);
    private void OnStaleUnit(object sender, SelectionChangedEventArgs args) => ConvertUnit(Model.Settings.IndexStale, Stale, ref _staleScale, StaleUnit.SelectedIndex switch { 1 => 24, 2 => 168, _ => 1 });
    private void ConvertUnit(Setting setting, TextBox? box, ref int scale, int next)
    {
        if (_refreshing || box is null)
            return;
        _refreshing = true;
        if (decimal.TryParse(box.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var value)
            && value >= 0 && value <= decimal.MaxValue / scale)
            box.Text = (value * scale / next).ToString("G", CultureInfo.CurrentCulture);
        scale = next;
        _refreshing = false;
        Stage(setting, box, scale);
        ShowStatus();
    }
}
