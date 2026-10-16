using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Networking.Connectivity;
using Windows.System;
using Windows.UI.Text;
using Windows.UI.ViewManagement;

namespace Syno.TinyTorrent.Settings;

public sealed partial class ConnectionPage : UserControl
{
    private readonly UISettings _display = new();
    private bool _composing;
    private readonly Speedometer _downloadMeter = new(Syno.Lucide.Download);
    private readonly Speedometer _uploadMeter = new(Syno.Lucide.Upload);
    private string? _meterId;
    private double? _meterScale;
    public ConnectionSetup Model { get; }
    public event EventHandler? ReturnRequested;
    internal event EventHandler? ParentRequested;

    public ConnectionPage(ConnectionSetup model)
    {
        Model = model;
        InitializeComponent();
        DownloadMeter.Content = _downloadMeter;
        UploadMeter.Content = _uploadMeter;
        foreach (var editor in new[] { Download, Upload })
        {
            editor.TextCompositionStarted += (_, _) => _composing = true;
            editor.TextCompositionEnded += (_, _) => _composing = false;
            editor.PreviewKeyDown += OnApplyKey;
        }
        Loaded += async (_, _) =>
        {
            Model.PropertyChanged += OnModel;
            RefreshHierarchy();
            RefreshMeters();
            _display.TextScaleFactorChanged += OnTextScale;
            NetworkInformation.NetworkStatusChanged += OnNetwork;
            UpdateColumns();
            await Model.RefreshLink();
        };
        Unloaded += (_, _) =>
        {
            Model.PropertyChanged -= OnModel;
            _display.TextScaleFactorChanged -= OnTextScale;
            NetworkInformation.NetworkStatusChanged -= OnNetwork;
        };
    }

    public static FontWeight Weight(bool changed) =>
        changed ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

    public static Visibility Hidden(bool visible) =>
        visible ? Visibility.Collapsed : Visibility.Visible;

    internal void FocusHeading() => Hierarchy.Focus(FocusState.Programmatic);

    private void OnModel(object? sender, PropertyChangedEventArgs args)
    {
        RefreshHierarchy();
        RefreshMeters();
    }

    private void RefreshMeters()
    {
        // Both meters share one scale, so equal speeds fill equal arcs. It only
        // grows during a test, so the needles do not jump, and fits the result
        // once the test completes, so a stale entered capacity does not stay.
        if (_meterId != Model.TestId || Model.TestId.Length == 0)
        {
            _meterId = Model.TestId;
            _meterScale = Speedometer.Ceiling(Model.DownloadCapacity, Model.UploadCapacity);
            _downloadMeter.Reset();
            _uploadMeter.Reset();
        }
        var reading = Speedometer.Ceiling(Model.DownloadRate, Model.UploadRate);
        if (Model.HasResult || reading > (_meterScale ?? 0))
            _meterScale = reading;
        _downloadMeter.Show(Model.DownloadRate, _meterScale, Model.Text.Get("connection_setup", "download_limit"), Model.Text);
        _uploadMeter.Show(Model.UploadRate, _meterScale, Model.Text.Get("connection_setup", "upload_limit"), Model.Text);
    }

    private void OnNetwork(object sender) => DispatcherQueue.TryEnqueue(async () => await Model.RefreshLink());

    private void RefreshHierarchy()
    {
        var labels = new[]
        {
            Model.Text.Get("settings", "limits"),
            Model.Text.Get("connection_setup", "title"),
        };
        if (Hierarchy.ItemsSource is not string[] current || !current.SequenceEqual(labels))
            Hierarchy.ItemsSource = labels;
    }

    private void OnHierarchy(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Index == 0)
            ParentRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void OnTest(object sender, RoutedEventArgs args) => await Model.Test();

    private async void OnCancelTest(object sender, RoutedEventArgs args) =>
        await Model.CancelTest();

    private async void OnResume(object sender, RoutedEventArgs args) => await Model.Resume();

    private void OnQuick(object sender, RoutedEventArgs args) => Model.LongerTest = false;

    private void OnLonger(object sender, RoutedEventArgs args) => Model.LongerTest = true;

    private void OnBodySize(object sender, SizeChangedEventArgs args) => UpdateColumns();

    private void OnTextScale(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(UpdateColumns);

    private void UpdateColumns()
    {
        var scale = _display.TextScaleFactor;
        TestActions.Width = 120 * scale;
        StatusArea.MinHeight = 112 * scale;
        var stacked = Body.ActualWidth < 880 * scale;
        Header.RowSpacing = stacked ? 16 : 0;
        Grid.SetColumn(HeaderFacts, stacked ? 0 : 1);
        Grid.SetRow(HeaderFacts, stacked ? 1 : 0);
        MeasurementBody.ColumnSpacing = stacked ? 0 : 24;
        StatusColumn.Width = stacked ? new GridLength(0) : new GridLength(240 * scale);
        Grid.SetColumn(StatusArea, stacked ? 0 : 1);
        Grid.SetRow(StatusArea, stacked ? 1 : 0);
        Grid.SetColumn(TestOptions, stacked ? 0 : 1);
        Grid.SetColumnSpan(TestOptions, stacked ? 3 : 1);
        Grid.SetRow(TestOptions, stacked ? 1 : 0);
        InputColumn.Width = stacked
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(336 * scale);
        PreviewColumn.Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Body.ColumnSpacing = stacked ? 0 : 16;
        Grid.SetColumn(Preview, stacked ? 0 : 1);
        Grid.SetRow(Preview, stacked ? 1 : 0);
    }

    private async void OnApplyKey(object sender, KeyRoutedEventArgs args)
    {
        if (args.Handled || args.Key != VirtualKey.Enter || _composing || !Model.CanApply)
            return;
        args.Handled = true;
        await Apply();
    }

    private async void OnApply(object sender, RoutedEventArgs args) => await Apply();

    private async Task Apply()
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
