using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
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

    private void UpdateStatus()
    {
        if (StatusBar.ActualWidth <= 0) return;
        var scale = _uiSettings.TextScaleFactor;
        var width = 224 * scale;
        var narrow = StatusBar.ActualWidth < 1200 * scale;
        DownloadRate.Width = UploadRate.Width = width;
        Rates.Orientation = narrow && 2 * width + Rates.Spacing + StatusActions.ActualWidth + StatusBar.ColumnSpacing > StatusBar.ActualWidth ?
            Orientation.Vertical : Orientation.Horizontal;
        Grid.SetColumnSpan(StatusActions, narrow ? 2 : 1);
        StatusActions.HorizontalAlignment = narrow ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        Grid.SetRow(StatusContext, narrow ? 1 : 0);
        Grid.SetColumn(StatusContext, narrow ? 0 : 2);
        Grid.SetColumnSpan(StatusContext, narrow ? 3 : 1);
        StatusContext.Margin = new Thickness(0, narrow ? 8 : 0, 0, 0);
        Incoming.TextAlignment = narrow ? TextAlignment.Left : TextAlignment.Right;
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
        var exclusions = new FrameworkElement[] { AppIcon, Menus, Search, AddButtons, ThemeButton }
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
        var content = 48 + Menus.ActualWidth + SearchArea.Margin.Left +
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
        BackText.Text = Model.Text.Get("menus", "back");
        RefreshMenus();
        AutomationProperties.SetName(Torrents, Model.Text.Get("window", "torrents"));
        NameColumn.DisplayName = Model.Text.Get("columns", "name");
        SizeColumn.DisplayName = Model.Text.Get("columns", "size");
        ProgressColumn.DisplayName = Model.Text.Get("columns", "progress");
        StatusColumn.DisplayName = Model.Text.Get("columns", "status");
        DownColumn.DisplayName = Model.Text.Get("columns", "down");
        UpColumn.DisplayName = Model.Text.Get("columns", "up");
        QueueColumn.DisplayName = Model.Text.Get("columns", "queue");
        EtaColumn.DisplayName = Model.Text.Get("columns", "eta");
        RatioColumn.DisplayName = Model.Text.Get("columns", "ratio");
        PeersColumn.DisplayName = Model.Text.Get("columns", "peers");
        AddedColumn.DisplayName = Model.Text.Get("columns", "added");
        Search.PlaceholderText = Model.Text.Get("finding", "placeholder");
        AutomationProperties.SetName(Search, Model.Text.Get("finding", "search"));
        Search.ItemsSource = Model.FindSuggestions(Search.Text);
        FiltersTitle.Text = Model.Text.Get("filters", "title");
        AutomationProperties.SetName(Filters, Model.Text.Get("filters", "title"));
        NameButton(FiltersClose, Model.Text.Get("filters", "close"));
        AutomationProperties.SetName(Split, Model.Text.Get("inspector", "resize"));
        AlternativeText.Text = Model.Text.Get("window", "alternative");
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
