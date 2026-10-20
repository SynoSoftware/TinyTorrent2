using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Syno.TinyTorrent.Controls;
using Windows.System;

namespace Syno.TinyTorrent.Settings;

public sealed partial class Scheduler : UserControl
{
    private readonly PeriodEditor _editor;
    public Schedule Model { get; }
    internal Control Map => Timeline;
    internal Control Selector => (Control)Mode.Content;
    internal Control Add => AddPeriod;

    internal Scheduler(Schedule model, LimitSelector selector)
    {
        Model = model;
        InitializeComponent();
        Mode.Content = selector;
        _editor = new PeriodEditor(model);
        _editor.Deleting += (_, _) => FocusAdd();
        var week = new Week(model);
        week.TimeChanged += OnTime;
        week.SizeChanged += OnMapSize;
        Timeline.Content = week;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        Model.PropertyChanged += OnModel;
        RefreshPeriods();
        ShowPeriod();
        UpdateFocus();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        Model.PropertyChanged -= OnModel;
    }

    private void OnMapSize(object sender, SizeChangedEventArgs args) => UpdateFocus();

    private void UpdateFocus()
    {
        if (Timeline.Content is not Week week)
            return;
        var bounds = WeekCard
            .TransformToVisual(week)
            .TransformBounds(
                new Windows.Foundation.Rect(0, 0, WeekCard.ActualWidth, WeekCard.ActualHeight)
            );
        week.FocusVisualMargin = new Thickness(
            bounds.Left,
            bounds.Top,
            week.ActualWidth - bounds.Right,
            week.ActualHeight - bounds.Bottom
        );
    }

    private void OnTime(object? sender, DateTime time)
    {
        var position = Model.Text.Format(
            "transfer_limits",
            "day_time",
            Model.ShortDay(Schedule.Weekday(time)),
            Schedule.Time(time.Hour * 60 + time.Minute)
        );
        Now.Text = Model.Text.Format("transfer_limits", "now", position);
        ToolTipService.SetToolTip(Now, Now.Text);
    }

    private void FocusAdd()
    {
        AddPeriod.StartBringIntoView();
        AddPeriod.Focus(FocusState.Programmatic);
    }

    private void OnModel(object? sender, PropertyChangedEventArgs args)
    {
        RefreshPeriods();
        // Expander raises Expanding before applying its visual state.
        DispatcherQueue.TryEnqueue(ShowPeriod);
    }

    private async void OnAdd(object sender, RoutedEventArgs args)
    {
        await Model.Add();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsLoaded || !Model.IsOpen)
                return;
            UpdateLayout();
            var row = Periods
                .Children.Cast<Expander>()
                .FirstOrDefault(row => row.Tag == Model.OpenPeriod);
            if (row is not null && FocusManager.FindFirstFocusableElement(row) is Control header)
                header.Focus(FocusState.Programmatic);
        });
    }

    private async void OnExpanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        if (sender.Tag is SchedulePeriod period)
            await Model.Open(period);
    }

    private async void OnCollapsed(Expander sender, ExpanderCollapsedEventArgs args)
    {
        if (sender.Tag is SchedulePeriod period)
            await Model.Close(period);
    }

    private void OnEditorKey(object sender, KeyRoutedEventArgs args)
    {
        if (
            args.Handled
            || args.Key != VirtualKey.Escape
            || sender is not Expander { IsExpanded: true } row
        )
            return;
        args.Handled = true;
        row.IsExpanded = false;
        if (FocusManager.FindFirstFocusableElement(row) is Control header)
            header.Focus(FocusState.Programmatic);
    }

    private void RefreshPeriods()
    {
        if (!IsLoaded)
            return;
        while (Periods.Children.Count > Model.Periods.Count)
        {
            ((Expander)Periods.Children[^1]).Content = null;
            Periods.Children.RemoveAt(Periods.Children.Count - 1);
        }
        while (Periods.Children.Count < Model.Periods.Count)
        {
            var row = new SettingsExpander
            {
                Padding = new Thickness(16, 0, 16, 0),
                Header = new PeriodHeader(),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            row.Expanding += OnExpanding;
            row.Collapsed += OnCollapsed;
            row.KeyDown += OnEditorKey;
            Periods.Children.Add(row);
        }
        for (var index = 0; index < Model.Periods.Count; index++)
        {
            var row = (Expander)Periods.Children[index];
            var period = Model.Periods[index];
            row.Tag = period;
            var header = (PeriodHeader)row.Header;
            header.When.Text = period.When;
            header.Behavior.Text = period.Behavior;
            foreach (var label in new[] { header.When, header.Behavior })
                ToolTipService.SetToolTip(label, label.Text);
            AutomationProperties.SetName(row, period.When + " · " + period.Behavior);
        }
    }

    private void ShowPeriod()
    {
        if (!IsLoaded || Model.IsPending)
            return;
        var selected = Periods
            .Children.Cast<Expander>()
            .FirstOrDefault(row => row.Tag == Model.OpenPeriod);
        foreach (Expander row in Periods.Children)
        {
            if (ReferenceEquals(row, selected))
                continue;
            row.IsExpanded = false;
            // Keep the closing drawer's editor until another row needs it.
            if (selected is not null)
                row.Content = null;
        }
        // Detach the shared editor from its old row before attaching it here.
        if (selected is not null)
        {
            selected.Content = _editor;
            selected.IsExpanded = true;
        }
        if (!Model.HasError)
            return;
        UpdateLayout();
        FrameworkElement feedback = Model.IsOpen ? _editor.Feedback : PeriodsRow;
        feedback.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
    }

    private sealed class PeriodHeader : Grid
    {
        internal TextBlock When { get; } = new() { TextTrimming = TextTrimming.CharacterEllipsis };
        internal TextBlock Behavior { get; } =
            new()
            {
                Style = (Style)Application.Current.Resources["TinyTorrentTitleDetailTextStyle"],
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

        internal PeriodHeader()
        {
            MinHeight = 68;
            ColumnSpacing = 24;
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            );
            Children.Add(
                new FontIcon
                {
                    FontFamily = Syno.Lucide.Font,
                    Glyph = Syno.Lucide.Clock,
                    FontSize = (double)Application.Current.Resources["TinyTorrentHeaderIconSize"],
                    VerticalAlignment = VerticalAlignment.Center,
                }
            );
            var labels = new StackPanel
            {
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { When, Behavior },
            };
            SetColumn(labels, 1);
            Children.Add(labels);
        }
    }
}
