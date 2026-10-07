using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI.ViewManagement;
using Syno.TinyTorrent.Models;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow
{
    private readonly UISettings _uiSettings = new();

    private void OnTextScaling(UISettings sender, object args) => DispatcherQueue.TryEnqueue(UpdateStatus);
    private void OnStatusSize(object sender, SizeChangedEventArgs args) => UpdateStatus();
    private void OnLimitsStatus(object sender, DoubleTappedRoutedEventArgs args) => Run(Model.Limits);
    private void OnUpdateAvailable(object sender, DoubleTappedRoutedEventArgs args) => Run(Model.OpenUpdate);

    private void OnIncoming(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (Model.MissingInterface.Length > 0) Model.ShowSetting(Model.Preferences.Interface);
        else if (Model.Preferences.Proxy.IsInUse) Model.ShowProxySetting();
        else Model.ShowSetting(Model.Preferences.Port);
    }

    private async void OnFilterStatus(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (!await ShowTorrents()) return;
        Model.IsFilterOpen = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Filters.Focus(FocusState.Programmatic));
    }

    // A rate keeps room for its longest text, so live rates never move the
    // items after it. Other labels shorten to their icons, least important
    // first, until the line fits.
    private void UpdateStatus()
    {
        if (StatusBar.ActualWidth <= 0) return;
        DownloadRate.Width = UploadRate.Width = 216 * _uiSettings.TextScaleFactor;
        TextBlock[] labels = [UpdateLabel, FilterLabel, IncomingLabel, AlternativeLabel, PausedLabel, CountLabel];
        foreach (var label in labels) label.Visibility = Visibility.Visible;
        foreach (var label in labels)
        {
            if (StatusFits()) return;
            label.Visibility = Visibility.Collapsed;
        }
    }

    private bool StatusFits()
    {
        var unlimited = new Size(double.PositiveInfinity, double.PositiveInfinity);
        TransferStatus.Measure(unlimited);
        ListStatus.Measure(unlimited);
        return TransferStatus.DesiredSize.Width + StatusBar.ColumnSpacing + ListStatus.DesiredSize.Width <= StatusBar.ActualWidth;
    }

    private void UpdateChrome()
    {
        if (_allowClose || Root.XamlRoot is null) return;
        var scale = Root.XamlRoot.RasterizationScale;
        var left = Math.Max(0, AppWindow.TitleBar.LeftInset) / scale;
        var right = Math.Max(0, AppWindow.TitleBar.RightInset) / scale;
        LeftInset.Width = new GridLength(left);
        RightInset.Width = new GridLength(right);
        UpdateMinimum(scale);
        if (Caption.ActualHeight <= 0) return;
        var start = AppWindow.TitleBar.LeftInset;
        var end = Math.Max(start, (int)Math.Round(Caption.ActualWidth * scale) - AppWindow.TitleBar.RightInset);
        var padding = (int)Math.Ceiling(4 * scale);
        var height = (int)Math.Round(Caption.ActualHeight * scale);
        var rectangles = new List<RectInt32>();
        var exclusions = new FrameworkElement[] { BackButton, AppIcon, Menus, Search, AddButtons, ThemeButton }
            .Where(control => control.Visibility == Visibility.Visible && control.ActualWidth > 0)
            .Select(GetRegion).OrderBy(bounds => bounds.X);
        foreach (var bounds in exclusions)
        {
            var edge = Math.Clamp(bounds.X - padding, start, end);
            if (edge > start) rectangles.Add(new RectInt32(start, 0, edge - start, height));
            start = Math.Clamp(bounds.X + bounds.Width + padding, start, end);
        }
        if (end > start) rectangles.Add(new RectInt32(start, 0, end - start, height));
        AppWindow.TitleBar.SetDragRectangles(rectangles.ToArray());
        InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
            .SetRegionRects(NonClientRegionKind.Icon, [GetRegion(AppIcon)]);
    }

    private RectInt32 GetRegion(FrameworkElement control)
    {
        var scale = Root.XamlRoot.RasterizationScale;
        var bounds = control.TransformToVisual(Root).TransformBounds(new Rect(0, 0, control.ActualWidth, control.ActualHeight));
        var left = (int)Math.Floor(bounds.Left * scale);
        var top = (int)Math.Floor(bounds.Top * scale);
        return new RectInt32(left, top, (int)Math.Ceiling(bounds.Right * scale) - left,
            (int)Math.Ceiling(bounds.Bottom * scale) - top);
    }

    private void UpdateMinimum(double scale)
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
        var content = 48 + BackButton.ActualWidth + Menus.ActualWidth + SearchArea.Margin.Left +
            Search.MinWidth + SearchArea.Margin.Right + AddButtons.ActualWidth + ThemeButton.Width;
        var frame = AppWindow.Size.Width - AppWindow.ClientSize.Width;
        var width = (int)Math.Ceiling(Math.Max(720, content + LeftInset.Width.Value + RightInset.Width.Value) * scale) + frame;
        var height = (int)Math.Ceiling(560 * scale);
        if (presenter.PreferredMinimumWidth != width) presenter.PreferredMinimumWidth = width;
        if (presenter.PreferredMinimumHeight != height) presenter.PreferredMinimumHeight = height;
    }

    private void UpdateColors()
    {
        if (_allowClose) return;
        var titleBar = AppWindow.TitleBar;
        titleBar.PreferredTheme = Root.ActualTheme == ElementTheme.Dark ? TitleBarTheme.Dark : TitleBarTheme.Light;
        titleBar.BackgroundColor = Colors.Transparent;
        titleBar.InactiveBackgroundColor = Colors.Transparent;
        titleBar.ForegroundColor = null;
        titleBar.InactiveForegroundColor = null;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = null;
        titleBar.ButtonInactiveForegroundColor = null;
        titleBar.ButtonHoverBackgroundColor = null;
        titleBar.ButtonHoverForegroundColor = null;
        titleBar.ButtonPressedBackgroundColor = null;
        titleBar.ButtonPressedForegroundColor = null;
        RefreshTheme();
    }

    private void RefreshTheme()
    {
        Model.IsDark = Root.ActualTheme == ElementTheme.Dark;
        ThemeIcon.Glyph = Model.IsDark ? Syno.Lucide.Sun : Syno.Lucide.Moon;
        NameButton(ThemeButton, Model.Text.Get("chrome", Model.IsDark ? "light" : "dark"));
    }

    private static void NameButton(Button button, string text)
    {
        AutomationProperties.SetName(button, text);
        ToolTipService.SetToolTip(button, text);
    }

    private void RefreshText()
    {
        Title = Model.Text.Get("window", "title");
        RefreshTheme();
        NameButton(BackButton, Model.Text.Format("shortcuts", "tip", Model.Text.Get("menus", "back"), ShortcutText(Model.ShowTorrents)));
        RefreshMenus();
        AutomationProperties.SetName(Torrents, Model.Text.Get("window", "torrents"));
        NameColumn.DisplayName = Model.Text.Get("columns", "name");
        SizeColumn.DisplayName = Model.Text.Get("columns", "size");
        ProgressColumn.DisplayName = Model.Text.Get("columns", "progress");
        StatusColumn.DisplayName = Model.Text.Get("columns", "status");
        DownColumn.DisplayName = Model.Text.Get("columns", "down");
        UpColumn.DisplayName = Model.Text.Get("columns", "up");
        LimitColumn.DisplayName = Model.Text.Get("columns", "limit");
        QueueColumn.DisplayName = Model.Text.Get("columns", "queue");
        EtaColumn.DisplayName = Model.Text.Get("columns", "eta");
        RatioColumn.DisplayName = Model.Text.Get("columns", "ratio");
        SeedsColumn.DisplayName = Model.Text.Get("columns", "seeds");
        PeersColumn.DisplayName = Model.Text.Get("columns", "peers");
        AddedColumn.DisplayName = Model.Text.Get("columns", "added");
        Search.PlaceholderText = Model.Text.Get("finding", "placeholder");
        AutomationProperties.SetName(Search, Model.Text.Get("finding", "search"));
        Search.ItemsSource = Model.FindSuggestions(Search.Text);
        FiltersTitle.Text = Model.Text.Get("filters", "title");
        AutomationProperties.SetName(Filters, Model.Text.Get("filters", "title"));
        NameButton(FiltersClose, Model.Text.Get("filters", "close"));
        AutomationProperties.SetName(Split, Model.Text.Get("inspector", "resize"));
        Torrents.Strings = Model.Text.Table;
        Root.FlowDirection = Model.Text.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        RefreshDialogs();
        _interaction?.RefreshText?.Invoke();
        Torrents.RefreshView();
    }

    private void RefreshDialogs()
    {
        if (_interaction?.Dialog is { } dialog)
        {
            dialog.RequestedTheme = Root.ActualTheme;
            dialog.FlowDirection = Root.FlowDirection;
        }
    }

}
