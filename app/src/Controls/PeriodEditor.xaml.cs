using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Views;

namespace Syno.TinyTorrent.Controls;

public sealed partial class PeriodEditor : UserControl
{
    private readonly Windows.UI.ViewManagement.UISettings _display = new();
    public Schedule Model { get; }
    internal event EventHandler? Removing;

    public PeriodEditor(Schedule model)
    {
        Model = model;
        InitializeComponent();
        Loaded += (_, _) => { _display.TextScaleFactorChanged += OnTextScale; UpdateTimes(); };
        Unloaded += (_, _) => _display.TextScaleFactorChanged -= OnTextScale;
    }

    internal FrameworkElement Feedback => Model.DaysMessage.Length > 0 ? DaysRow : PeriodError;

    private void OnTextScale(Windows.UI.ViewManagement.UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(UpdateTimes);

    private void OnTimesSize(object sender, SizeChangedEventArgs args) => UpdateTimes();

    private void UpdateTimes()
    {
        var available = new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity);
        StartPair.Measure(available);
        EndPair.Measure(available);
        Remove.Measure(available);
        var pairWidth = Math.Max(StartPair.DesiredSize.Width, EndPair.DesiredSize.Width);
        var stacked = Times.ActualWidth < pairWidth * 2 + 16;
        var removeBelow = Times.ActualWidth < pairWidth * 2 + Remove.DesiredSize.Width + 32;
        Times.ColumnSpacing = stacked ? 0 : 16;
        Times.RowSpacing = removeBelow ? 16 : 0;
        var rows = stacked ? 3 : removeBelow ? 2 : 1;
        var columns = stacked ? 1 : removeBelow ? 2 : 3;
        while (Times.RowDefinitions.Count > rows)
            Times.RowDefinitions.RemoveAt(Times.RowDefinitions.Count - 1);
        while (Times.RowDefinitions.Count < rows)
            Times.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        while (Times.ColumnDefinitions.Count > columns)
            Times.ColumnDefinitions.RemoveAt(Times.ColumnDefinitions.Count - 1);
        while (Times.ColumnDefinitions.Count < columns)
            Times.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var index = 0; index < Times.ColumnDefinitions.Count; index++)
            Times.ColumnDefinitions[index].Width = index == Times.ColumnDefinitions.Count - 1
                ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        Grid.SetColumn(EndPair, stacked ? 0 : 1);
        Grid.SetRow(EndPair, stacked ? 1 : 0);
        Grid.SetColumn(Remove, removeBelow ? 0 : 2);
        Grid.SetRow(Remove, stacked ? 2 : removeBelow ? 1 : 0);
        Grid.SetColumnSpan(Remove, !stacked && removeBelow ? 2 : 1);
    }

    private async void OnRemove(object sender, RoutedEventArgs args)
    {
        if (Model.OpenPeriod is not { } period)
            return;
        Removing?.Invoke(this, EventArgs.Empty);
        await Model.Remove(period);
    }
}
