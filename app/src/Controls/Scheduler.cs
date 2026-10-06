using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Views;

namespace Syno.TinyTorrent.Controls;

public sealed partial class Scheduler : UserControl
{
    private PeriodDraft? _editor;
    private bool _refreshing;
    public Preferences Model { get; }
    public event EventHandler? SpeedRequested;

    public Scheduler(Preferences model)
    {
        Model = model;
        InitializeComponent();
        Timeline.Content = new Week(model);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        RefreshText();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        Model.PropertyChanged += OnModel;
        Model.TextChanged += OnText;
        Model.WeekChanged += OnWeek;
        RefreshText();
        RefreshPeriods();
        OnModel(this, new(string.Empty));
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        Model.PropertyChanged -= OnModel;
        Model.TextChanged -= OnText;
        Model.WeekChanged -= OnWeek;
    }

    internal void FocusAdd()
    {
        AddPeriod.StartBringIntoView();
        AddPeriod.Focus(FocusState.Programmatic);
    }

    private void OnModel(object? sender, PropertyChangedEventArgs args)
    {
        Hint.Visibility = Model.IsEditing ? Visibility.Collapsed : Visibility.Visible;
        if (_editor == Model.Draft) return;
        _editor = Model.Draft;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsLoaded) return;
            if (Model.IsEditing)
            {
                PeriodEditor.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
                StartTime.Focus(FocusState.Programmatic);
            }
            else if (Model.Selection is not null) ((Week)Timeline.Content).Focus(FocusState.Keyboard);
            else FocusAdd();
        });
    }

    private void ArrangeTimes(object sender, SizeChangedEventArgs args)
    {
        var available = new Size(double.PositiveInfinity, double.PositiveInfinity);
        StartTime.Measure(available);
        EndTime.Measure(available);
        var wide = Times.ActualWidth >= StartTime.DesiredSize.Width + EndTime.DesiredSize.Width + Times.ColumnSpacing;
        Grid.SetColumnSpan(StartTime, wide ? 1 : 2);
        Grid.SetColumnSpan(EndTime, wide ? 1 : 2);
        Grid.SetColumn(EndTime, wide ? 1 : 0);
        Grid.SetRow(EndTime, wide ? 0 : 1);
    }

    private void ArrangeSelection(object sender, SizeChangedEventArgs args)
    {
        SelectionActions.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var wide = SelectionLayout.ActualWidth >= SelectionActions.DesiredSize.Width + 280;
        Grid.SetColumnSpan(SelectionText, wide ? 1 : 2);
        Grid.SetColumn(SelectionActions, wide ? 1 : 0);
        Grid.SetRow(SelectionActions, wide ? 0 : 1);
    }

    protected override void OnKeyDown(KeyRoutedEventArgs args)
    {
        base.OnKeyDown(args);
        if (args.Handled || args.Key != VirtualKey.Escape || !Model.IsEditing || !Model.CancelPeriod.CanExecute(null)) return;
        Model.CancelPeriod.Execute(null);
        args.Handled = true;
    }

    private void OnText(object? sender, EventArgs args) => RefreshText();
    private void OnWeek(object? sender, EventArgs args) => RefreshPeriods();

    private void RefreshPeriods()
    {
        var alternative = Model.Periods.Where(period => period.Mode == ScheduleMode.Alternative).ToArray();
        var paused = Model.Periods.Where(period => period.Mode == ScheduleMode.Paused).ToArray();
        if (AlternativePeriods.ItemsSource is not SchedulePeriod[] previous || !previous.SequenceEqual(alternative))
            AlternativePeriods.ItemsSource = alternative;
        if (PausedPeriods.ItemsSource is not SchedulePeriod[] earlier || !earlier.SequenceEqual(paused))
            PausedPeriods.ItemsSource = paused;
    }

    private void RefreshText()
    {
        _refreshing = true;
        Label(AddPeriod, "add_period");
        Label(SavePeriod, "save_period");
        Label(SpeedLimits, "speed");
        CancelPeriod.Content = Model.Text.Get("add", "cancel");
        DaysTitle.Text = Model.Text.Get("preferences", "start_days");
        StartTime.Header = Model.Text.Get("preferences", "start_time");
        EndTime.Header = Model.Text.Get("preferences", "end_time");
        AutomationProperties.SetName(StartTime, (string)StartTime.Header);
        AutomationProperties.SetName(EndTime, (string)EndTime.Header);
        PeriodMode.Header = Model.Text.Get("preferences", "period_mode");
        AutomationProperties.SetName(PeriodMode, (string)PeriodMode.Header);
        Label(AlternativeMode, "alternative");
        Label(PausedMode, "paused");
        NormalLegend.Text = Model.Text.Get("preferences", "normal");
        AlternativeLegend.Text = Model.Text.Get("preferences", "alternative");
        PausedLegend.Text = Model.Text.Get("preferences", "paused");
        Hint.Text = Model.Text.Get("preferences", "timeline_hint");
        PreviewHint.Text = Model.Text.Get("preferences", "preview_hint");
        _refreshing = false;
    }

    private void Label(ContentControl control, string key) => control.Content = Model.Text.Get("preferences", key);
    private async void OnToggle(object sender, RoutedEventArgs args) => await Model.Toggle(Model.Schedule, Enabled.IsOn);
    private void OnSpeedLimits(object sender, RoutedEventArgs args) => SpeedRequested?.Invoke(this, EventArgs.Empty);
    public static int ModeIndex(bool paused) => paused ? 1 : 0;
    public static Visibility MessageVisibility(string value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

    private void OnPeriodMode(object sender, SelectionChangedEventArgs args)
    {
        if (!_refreshing && Model.Draft is { } draft) draft.IsPaused = PeriodMode.SelectedIndex == 1;
    }
}
