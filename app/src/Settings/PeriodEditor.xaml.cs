using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Syno.TinyTorrent.Settings;

public sealed partial class PeriodEditor : UserControl
{
    private readonly Windows.UI.ViewManagement.UISettings _display = new();
    public Schedule Model { get; }
    internal event EventHandler? Deleting;

    public PeriodEditor(Schedule model)
    {
        Model = model;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _display.TextScaleFactorChanged += OnTextScale;
            UpdateTimes();
        };
        Unloaded += (_, _) => _display.TextScaleFactorChanged -= OnTextScale;
    }

    internal FrameworkElement Feedback => Model.DaysMessage.Length > 0 ? DaysRow : PeriodError;

    private void OnTextScale(Windows.UI.ViewManagement.UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(UpdateTimes);

    private void OnTimesSize(object sender, SizeChangedEventArgs args) => UpdateTimes();

    private void UpdateTimes()
    {
        var available = new Windows.Foundation.Size(
            double.PositiveInfinity,
            double.PositiveInfinity
        );
        StartPair.Measure(available);
        EndPair.Measure(available);
        Delete.Measure(available);
        var pairWidth = Math.Max(StartPair.DesiredSize.Width, EndPair.DesiredSize.Width);
        var stacked = Times.ActualWidth < pairWidth * 2 + 16;
        var deleteBelow = Times.ActualWidth < pairWidth * 2 + Delete.DesiredSize.Width + 32;
        Times.ColumnSpacing = stacked ? 0 : 16;
        Times.RowSpacing = deleteBelow ? 16 : 0;
        var rows =
            stacked ? 3
            : deleteBelow ? 2
            : 1;
        var columns =
            stacked ? 1
            : deleteBelow ? 2
            : 3;
        while (Times.RowDefinitions.Count > rows)
            Times.RowDefinitions.RemoveAt(Times.RowDefinitions.Count - 1);
        while (Times.RowDefinitions.Count < rows)
            Times.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        while (Times.ColumnDefinitions.Count > columns)
            Times.ColumnDefinitions.RemoveAt(Times.ColumnDefinitions.Count - 1);
        while (Times.ColumnDefinitions.Count < columns)
            Times.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            );
        for (var index = 0; index < Times.ColumnDefinitions.Count; index++)
            Times.ColumnDefinitions[index].Width =
                index == Times.ColumnDefinitions.Count - 1
                    ? new GridLength(1, GridUnitType.Star)
                    : GridLength.Auto;
        Grid.SetColumn(EndPair, stacked ? 0 : 1);
        Grid.SetRow(EndPair, stacked ? 1 : 0);
        Grid.SetColumn(Delete, deleteBelow ? 0 : 2);
        Grid.SetRow(
            Delete,
            stacked ? 2
                : deleteBelow ? 1
                : 0
        );
        Grid.SetColumnSpan(Delete, !stacked && deleteBelow ? 2 : 1);
    }

    private async void OnDelete(object sender, RoutedEventArgs args)
    {
        if (Model.OpenPeriod is not { } period)
            return;
        Deleting?.Invoke(this, EventArgs.Empty);
        await Model.Delete(period);
    }
}
