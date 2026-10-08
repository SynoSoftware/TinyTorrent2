using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Syno.TinyTorrent.Views;
using Windows.System;

namespace Syno.TinyTorrent.Controls;

public sealed partial class Scheduler : UserControl
{
    private readonly PeriodEditor _editor;
    public Schedule Model { get; }
    public MainViewModel Main { get; }

    public Scheduler(Schedule model, MainViewModel main)
    {
        Model = model;
        Main = main;
        InitializeComponent();
        _editor = new PeriodEditor(model);
        _editor.Removing += (_, _) => FocusAdd();
        Timeline.Content = new Week(model, main);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        Model.PropertyChanged += OnModel;
        RefreshPeriods();
        ShowPeriod();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        Model.PropertyChanged -= OnModel;
    }

    internal void FocusAdd()
    {
        AddPeriod.StartBringIntoView();
        AddPeriod.Focus(FocusState.Programmatic);
    }

    internal Control Editor(string? name) => _editor.Editor(name);

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
            var row = new Expander
            {
                Padding = new Thickness(16, 0, 16, 0),
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
            row.Header = period.Description;
            AutomationProperties.SetName(row, period.Description);
        }
    }

    private void ShowPeriod()
    {
        if (!IsLoaded || Model.IsPending)
            return;
        Expander? selected = null;
        foreach (Expander row in Periods.Children)
        {
            if (row.Tag == Model.OpenPeriod)
            {
                selected = row;
                continue;
            }
            row.Content = null;
            row.IsExpanded = false;
        }
        // Detach the shared editor from its old row before attaching it here.
        if (selected is not null)
        {
            selected.Content = _editor;
            selected.IsExpanded = true;
        }
        if (!Model.HasScheduleError)
            return;
        UpdateLayout();
        FrameworkElement feedback = Model.IsOpen ? _editor.Feedback : PeriodsRow;
        feedback.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
    }
}
