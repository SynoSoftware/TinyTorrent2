using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.Text;
using Windows.UI.ViewManagement;

namespace Syno.TinyTorrent.Views;

public sealed partial class ConnectionPage : UserControl
{
    private readonly UISettings _display = new();
    public ConnectionSetup Model { get; }
    public event EventHandler? ReturnRequested;

    public ConnectionPage(ConnectionSetup model)
    {
        Model = model;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _display.TextScaleFactorChanged += OnTextScale;
            UpdateColumns();
            Download.Focus(FocusState.Programmatic);
        };
        Unloaded += (_, _) => _display.TextScaleFactorChanged -= OnTextScale;
    }

    public static FontWeight Weight(bool changed) => changed
        ? Microsoft.UI.Text.FontWeights.SemiBold
        : Microsoft.UI.Text.FontWeights.Normal;

    public static Visibility Hidden(bool visible) => visible
        ? Visibility.Collapsed : Visibility.Visible;

    private async void OnTest(object sender, RoutedEventArgs args) => await Model.Test();

    private async void OnCancelTest(object sender, RoutedEventArgs args) => await Model.CancelTest();

    private void OnBodySize(object sender, SizeChangedEventArgs args) => UpdateColumns();

    private void OnTextScale(UISettings sender, object args) => DispatcherQueue.TryEnqueue(UpdateColumns);

    private void UpdateColumns()
    {
        var scale = _display.TextScaleFactor;
        var stacked = Body.ActualWidth < 880 * scale;
        InputColumn.Width = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(320 * scale);
        PreviewColumn.Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Body.ColumnSpacing = stacked ? 0 : 24;
        Grid.SetColumn(Preview, stacked ? 0 : 1);
        Grid.SetRow(Preview, stacked ? 1 : 0);
    }

    private async void OnApply(object sender, RoutedEventArgs args)
    {
        if (await Model.Apply())
            ReturnRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCancel(object sender, RoutedEventArgs args)
    {
        if (Model.CanLeave)
            ReturnRequested?.Invoke(this, EventArgs.Empty);
    }
}
