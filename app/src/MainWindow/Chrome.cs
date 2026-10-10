using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Syno.TinyTorrent.Models;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow
{
    private readonly UISettings _uiSettings = new();
    private (string Language, double Scale, ElementTheme Theme, double Expanded, double Compact)? _pageMeasurement;

    private void OnTextScaling(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateStatus();
            UpdateChrome();
        });

    private void OnStatusSize(object sender, SizeChangedEventArgs args) => UpdateStatus();

    private void OnLimitsStatus(object sender, DoubleTappedRoutedEventArgs args) =>
        Run(Model.Limits);

    private void OnUpdateAvailable(object sender, DoubleTappedRoutedEventArgs args) =>
        Run(Model.OpenUpdate);

    // Resume all is a command, which a double-click on status must not run, so
    // the person's own pause opens nothing.
    private void OnRestriction(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (Model.PausedBy == PauseReason.Adapter)
            Model.ShowSetting(Model.Settings.Adapter);
        else if (Model.PausedBy != PauseReason.Manual)
            Run(Model.Limits);
    }

    private void OnIncoming(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (Model.MissingAdapter.Length > 0)
            Model.ShowSetting(Model.Settings.Adapter);
        else if (Model.Settings.Proxy.IsInUse)
            Model.ShowProxySetting();
        else
            Model.ShowSetting(Model.Settings.Port);
    }

    private async void OnFilterStatus(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (Model.Page == WindowPage.Library)
        {
            Model.IsFilterOpen = true;
            LibraryFilters.Focus(FocusState.Programmatic);
            return;
        }
        if (!await ShowTorrents())
            return;
        Model.IsFilterOpen = true;
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => Filters.Focus(FocusState.Programmatic)
        );
    }

    // A rate keeps room for its longest text, so live rates never move the
    // items after it. Other labels shorten to their icons, least important
    // first, until the line fits.
    private void UpdateStatus()
    {
        if (StatusBar.ActualWidth <= 0)
            return;
        DownloadRate.Width = UploadRate.Width = 216 * _uiSettings.TextScaleFactor;
        ReserveIncoming();
        ReserveFilter();
        TextBlock[] labels =
        [
            UpdateLabel,
            FreeSpaceLabel,
            FilterLabel,
            ExternalIpLabel,
            IncomingLabel,
            RestrictionLabel,
            CountLabel,
        ];
        foreach (var label in labels)
            label.Visibility = Visibility.Visible;
        foreach (var label in labels)
        {
            if (StatusFits())
                return;
            label.Visibility = Visibility.Collapsed;
        }
    }

    // The connection label changes text with the connection state, and the
    // labels before it would move with it, so it keeps room for its longest text.
    private void ReserveIncoming()
    {
        var width = 0.0;
        foreach (
            var key in new[]
            {
                "incoming_status",
                "no_incoming_status",
                "proxy_status",
                "no_adapter_status",
            }
        )
        {
            var probe = new TextBlock
            {
                Style = IncomingLabel.Style,
                Text = Model.Text.Get("window", key),
            };
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            width = Math.Max(width, probe.DesiredSize.Width);
        }
        if (IncomingLabel.MinWidth != width)
            IncomingLabel.MinWidth = width;
    }

    // The filter's count changes as torrents change state, and the labels
    // before it would move with it, so it keeps room for its largest count.
    private void ReserveFilter()
    {
        var probe = new TextBlock { Style = FilterLabel.Style, Text = Model.WidestFilterStatus };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = Math.Min(probe.DesiredSize.Width, FilterLabel.MaxWidth);
        if (FilterLabel.MinWidth != width)
            FilterLabel.MinWidth = width;
    }

    private bool StatusFits()
    {
        var unlimited = new Size(double.PositiveInfinity, double.PositiveInfinity);
        TransferStatus.Measure(unlimited);
        ListStatus.Measure(unlimited);
        return TransferStatus.DesiredSize.Width
                + StatusBar.ColumnSpacing
                + ListStatus.DesiredSize.Width
            <= StatusBar.ActualWidth;
    }

    private void UpdateChrome()
    {
        if (_allowClose || Root.XamlRoot is null)
            return;
        var scale = Root.XamlRoot.RasterizationScale;
        var left = Math.Max(0, AppWindow.TitleBar.LeftInset) / scale;
        var right = Math.Max(0, AppWindow.TitleBar.RightInset) / scale;
        LeftInset.Width = new GridLength(left);
        RightInset.Width = new GridLength(right);
        UpdateMinimum(scale);
        TitleDownload.Width = TitleUpload.Width = 128 * _uiSettings.TextScaleFactor;
        var speedWidth = TitleDownload.Width + TitleUpload.Width + TitleSpeeds.Spacing;
        var available = Caption.ActualWidth
            - left
            - right
            - CaptionStart.ActualWidth
            - Menus.ActualWidth
            - SettingsButton.ActualWidth
            - ThemeButton.ActualWidth
            - SearchArea.Margin.Left
            - SearchArea.Margin.Right;
        var pages = MeasurePages();
        var compact = available - pages.Expanded < Search.MinWidth;
        TorrentsPage.Text = compact ? string.Empty : Model.Text.Get("window", "torrents");
        LibraryPage.Text = compact ? string.Empty : Model.Text.Get("library", "title");
        available -= compact ? pages.Compact : pages.Expanded;
        TitleSpeeds.Visibility =
            Model.ShowsTitleSpeeds
            && available >= Search.MinWidth + speedWidth + TitleSpeeds.Margin.Left
                ? Visibility.Visible
                : Visibility.Collapsed;
        Search.Width = Math.Clamp(
            available - (TitleSpeeds.Visibility == Visibility.Visible ? speedWidth + TitleSpeeds.Margin.Left : 0),
            Search.MinWidth,
            Search.MaxWidth
        );
        if (Caption.ActualHeight <= 0)
            return;
        var start = AppWindow.TitleBar.LeftInset;
        var end = Math.Max(
            start,
            (int)Math.Round(Caption.ActualWidth * scale) - AppWindow.TitleBar.RightInset
        );
        var padding = (int)Math.Ceiling(4 * scale);
        var height = (int)Math.Round(Caption.ActualHeight * scale);
        var rectangles = new List<RectInt32>();
        var exclusions = new FrameworkElement[]
        {
            BackButton,
            AppIcon,
            Menus,
            Pages,
            Search,
            SettingsButton,
            ThemeButton,
        }
            .Where(control => control.Visibility == Visibility.Visible && control.ActualWidth > 0)
            .Select(GetRegion)
            .OrderBy(bounds => bounds.X);
        foreach (var bounds in exclusions)
        {
            var edge = Math.Clamp(bounds.X - padding, start, end);
            if (edge > start)
                rectangles.Add(new RectInt32(start, 0, edge - start, height));
            start = Math.Clamp(bounds.X + bounds.Width + padding, start, end);
        }
        if (end > start)
            rectangles.Add(new RectInt32(start, 0, end - start, height));
        AppWindow.TitleBar.SetDragRectangles(rectangles.ToArray());
        InputNonClientPointerSource
            .GetForWindowId(AppWindow.Id)
            .SetRegionRects(NonClientRegionKind.Icon, [GetRegion(AppIcon)]);
    }

    private RectInt32 GetRegion(FrameworkElement control)
    {
        var scale = Root.XamlRoot.RasterizationScale;
        var bounds = control
            .TransformToVisual(Root)
            .TransformBounds(new Rect(0, 0, control.ActualWidth, control.ActualHeight));
        var left = (int)Math.Floor(bounds.Left * scale);
        var top = (int)Math.Floor(bounds.Top * scale);
        return new RectInt32(
            left,
            top,
            (int)Math.Ceiling(bounds.Right * scale) - left,
            (int)Math.Ceiling(bounds.Bottom * scale) - top
        );
    }

    private (double Expanded, double Compact) MeasurePages()
    {
        if (_pageMeasurement is { } measured && measured.Language == Model.Text.Language &&
            measured.Scale == _uiSettings.TextScaleFactor && measured.Theme == Pages.ActualTheme)
            return (measured.Expanded, measured.Compact);
        // Measuring both label states on every layout keeps invalidating the selector.
        var torrents = TorrentsPage.Text;
        var library = LibraryPage.Text;
        TorrentsPage.Text = Model.Text.Get("window", "torrents");
        LibraryPage.Text = Model.Text.Get("library", "title");
        Pages.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var expanded = Pages.DesiredSize.Width;
        TorrentsPage.Text = LibraryPage.Text = string.Empty;
        Pages.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var compact = Pages.DesiredSize.Width;
        TorrentsPage.Text = torrents;
        LibraryPage.Text = library;
        if (Pages.XamlRoot is not null)
            _pageMeasurement = (Model.Text.Language, _uiSettings.TextScaleFactor, Pages.ActualTheme, expanded, compact);
        return (expanded, compact);
    }

    private void UpdateMinimum(double scale)
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
            return;
        var content =
            48
            + BackButton.ActualWidth
            + Menus.ActualWidth
            + MeasurePages().Compact
            + SearchArea.Margin.Left
            + Search.MinWidth
            + SearchArea.Margin.Right
            + SettingsButton.Width
            + ThemeButton.Width;
        var frame = AppWindow.Size.Width - AppWindow.ClientSize.Width;
        var minimum = Model.Page == WindowPage.Settings && _settingsPage is { } settings
            ? settings.MeasureWidth()
            : 720;
        var width =
            (int)
                Math.Ceiling(
                    Math.Max(minimum, content + LeftInset.Width.Value + RightInset.Width.Value) * scale
                ) + frame;
        var height = (int)Math.Ceiling(560 * scale);
        if (presenter.PreferredMinimumWidth != width)
            presenter.PreferredMinimumWidth = width;
        if (presenter.PreferredMinimumHeight != height)
            presenter.PreferredMinimumHeight = height;
        if (Model.Page == WindowPage.Settings && AppWindow.Size.Width < width)
            AppWindow.Resize(new SizeInt32(width, AppWindow.Size.Height));
    }

    private void UpdateColors()
    {
        if (_allowClose)
            return;
        var titleBar = AppWindow.TitleBar;
        titleBar.PreferredTheme =
            Root.ActualTheme == ElementTheme.Dark ? TitleBarTheme.Dark : TitleBarTheme.Light;
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
        NameButton(
            SettingsButton,
            Model.Text.Format(
                "shortcuts",
                "tip",
                Model.Text.Get("commands", "settings"),
                ShortcutText(Model.ShowSettings)
            )
        );
        NameButton(
            BackButton,
            Model.Text.Format(
                "shortcuts",
                "tip",
                Model.Text.Get("menus", "back"),
                ShortcutText(Back)
            )
        );
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
        Search.PlaceholderText = Model.Text.Get(Model.Page == WindowPage.Library ? "library" : "finding",
            Model.Page == WindowPage.Library ? "search" : "placeholder");
        AutomationProperties.SetName(Search, Model.Text.Get("finding", "search"));
        Search.ItemsSource = Model.FindSuggestions(Search.Text);
        FiltersTitle.Text = Model.Text.Get("filters", "title");
        AutomationProperties.SetName(Filters, Model.Text.Get("filters", "title"));
        NameButton(FiltersClose, Model.Text.Get("filters", "close"));
        AutomationProperties.SetName(Split, Model.Text.Get("inspector", "resize"));
        Torrents.Strings = Model.Text.Table;
        Root.FlowDirection = Model.Text.IsRightToLeft
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;
        RefreshDialogs();
        _interaction?.RefreshText?.Invoke();
        Torrents.RefreshView();
        RefreshNavigation();
        UpdateChrome();
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
