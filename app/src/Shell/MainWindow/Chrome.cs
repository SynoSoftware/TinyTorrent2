using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace Syno.TinyTorrent.Shell;

public sealed partial class MainWindow
{
    private readonly UISettings _uiSettings = new();
    private bool _dragPending;
    private RectInt32[]? _dragRegions;
    private readonly TextBlock _measureProbe = new();

    private void OnTextScaling(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateStatus();
            UpdateChrome();
        });

    private bool _statusPending;

    private void QueueStatus()
    {
        if (_statusPending || _allowClose)
            return;
        _statusPending = DispatcherQueue.TryEnqueue(() =>
        {
            _statusPending = false;
            if (!_allowClose)
                UpdateStatus();
        });
    }

    private void UpdateStatus()
    {
        if (_allowClose || StatusBar.ActualWidth <= 0)
            return;
        RefreshWork();
        try
        {
            LayoutStatus();
        }
        finally
        {
            var context = ContextStatus.Visibility == Visibility.Visible;
            var activity =
                WorkButton.Visibility == Visibility.Visible
                || InformationStatus.Visibility == Visibility.Visible
                || _workViews.Values.Any(views => views.Inline.Visibility == Visibility.Visible);
            var transfer = TransferStatus
                .Children.OfType<FrameworkElement>()
                .Any(control => control.Visibility == Visibility.Visible);
            ContextGap.MinWidth = context && (activity || transfer) ? 16 : 0;
            ContextGap.Width =
                activity || transfer ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            TransferGap.MinWidth = activity && transfer ? 16 : 0;
            TransferGap.Width = activity ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            RecoverWorkFocus();
        }
    }

    private void LayoutStatus()
    {
        var library = Model.Page == WindowPage.Library;
        var about = Model.IsAbout;
        var current = !Model.IsLoading && !Model.IsStorageFailed;
        var transfers = Model.Torrents.Count > 0 || Model.IsLoading;
        var work = Model.Work.Items.Count > 0;
        var selected = library ? Model.Library.Selection.Items.Count : Model.Selected.Count;
        foreach (var label in new[] { CountLabel, InformationLabel, RestrictionLabel })
            label.MaxWidth = double.PositiveInfinity;
        TransferStatus.Spacing = 16;
        ContextIcon.Glyph =
            about ? Syno.Lucide.Info
            : library ? Model.Library.StatusGlyph
            : Model.Page == WindowPage.Settings ? Syno.Lucide.Settings
            : Syno.Lucide.List;
        SetStatus(CountLabel, Model.CountStatus);
        SetStatus(InformationLabel, Model.Library.InformationStatus);
        var downloadTip = about
            ? Model.Text.Format("about", "downloaded_hint", Model.LifetimeDownloaded)
            : Model.DownloadTip;
        var uploadTip = about
            ? Model.Text.Format("about", "uploaded_hint", Model.LifetimeUploaded)
            : Model.UploadTip;
        SetStatus(
            DownloadLabel,
            about
                ? Model.Text.Format("about", "downloaded", Model.LifetimeDownloaded)
                : Model.DownloadStatus,
            downloadTip
        );
        SetStatus(
            UploadLabel,
            about
                ? Model.Text.Format("about", "uploaded", Model.LifetimeUploaded)
                : Model.UploadStatus,
            uploadTip
        );
        SetStatus(
            TransferLabel,
            about
                ? Model.Text.Get("settings", "lifetime_totals")
                : Model.Text.Get("window", "torrents")
        );
        SetStatus(MemoryCaption, Model.Text.Get("about", "memory"));
        SetStatus(
            EngineMemoryLabel,
            Model.Text.Format("about", "engine_memory", Model.EngineMemory),
            Model.Text.Format("about", "engine_memory_hint", Model.EngineMemory)
        );
        SetStatus(
            UiMemoryLabel,
            Model.Text.Format("about", "ui_memory", Model.UiMemory),
            Model.Text.Format("about", "ui_memory_hint", Model.UiMemory)
        );
        SetStatus(RestrictionLabel, Model.RestrictionStatus, Model.RestrictionTip);
        SetStatus(LimitsLabel, Model.Text.Get("window", "limits_on"));
        SetStatus(
            FreeSpaceLabel,
            Model.FreeSpaceStatus,
            Model.Text.Format(
                "window",
                "space_path",
                Model.Settings.Destination.ConfirmedText,
                Model.FreeSpaceStatus
            )
        );
        SetStatus(ExternalIpLabel, Model.ExternalIp);
        SetStatus(IncomingLabel, Model.IncomingStatus, Model.Incoming);
        SetStatus(
            UpdateLabel,
            Model.Text.Get("about", "available"),
            Model.Text.Get("about", "available_tip")
        );

        ShowStatus(ContextStatus, Model.CountStatus.Length > 0);
        ShowStatus(ContextIcon, true);
        ShowStatus(
            InformationStatus,
            library && current && !work && Model.Library.InformationStatus.Length > 0
        );
        ShowStatus(MemoryStatus, Model.ShowsMemory);
        ShowStatus(DownloadRate, about || transfers);
        ShowStatus(UploadRate, about || transfers);
        ShowStatus(RestrictionStatus, !about && current && Model.HasRestriction);
        ShowStatus(
            TransferLabel,
            about || library && (transfers || current && Model.HasRestriction)
        );
        ShowStatus(LimitsLabel, false);
        ShowStatus(FreeSpaceStatus, !about && Model.ShowsFreeSpace);
        ShowStatus(ExternalIpStatus, !about && current && Model.ShowsExternalIp);
        ShowStatus(Incoming, current && Model.Page == WindowPage.Torrents);
        ShowStatus(UpdateAvailable, !about && Model.HasUpdate);
        // MemoryStatus yields first, so while it shows, nothing after it
        // changes visibility and the divider can be decided here.
        ShowStatus(
            MemoryDivider,
            TransferStatus
                .Children.OfType<FrameworkElement>()
                .Any(control => control != MemoryStatus && control.Visibility == Visibility.Visible)
        );
        ReserveDigits(EngineMemory, EngineMemoryLabel);
        ReserveDigits(UiMemory, UiMemoryLabel);
        ReserveDigits(DownloadRate, DownloadLabel);
        ReserveDigits(UploadRate, UploadLabel);
        if (StatusFits())
            return;

        foreach (
            var control in new FrameworkElement[]
            {
                MemoryStatus,
                UpdateAvailable,
                FreeSpaceStatus,
                ExternalIpStatus,
                Incoming,
                InformationStatus,
            }
        )
        {
            ShowStatus(control, false);
            if (StatusFits())
                return;
        }
        FitWork();
        SetStatus(
            DownloadLabel,
            about ? Model.LifetimeDownloaded : Model.DownloadSpeed,
            downloadTip
        );
        SetStatus(UploadLabel, about ? Model.LifetimeUploaded : Model.UploadSpeed, uploadTip);
        ShowStatus(
            LimitsLabel,
            !about && transfers && Model.HasCaps && !Model.ShowsAlternativeLimits
        );
        ReserveDigits(DownloadRate, DownloadLabel);
        ReserveDigits(UploadRate, UploadLabel);
        if (StatusFits())
            return;

        ShowStatus(DownloadRate, false);
        ShowStatus(UploadRate, false);
        ShowStatus(LimitsLabel, false);
        ShowStatus(TransferLabel, library && RestrictionStatus.Visibility == Visibility.Visible);
        if (StatusFits())
            return;

        FitWork(minimal: true);
        ShowStatus(ContextIcon, false);
        if (StatusFits())
            return;

        if (!work)
        {
            CountLabel.MaxWidth = Math.Max(
                0,
                MeasureText(CountLabel, CountLabel.Text)
                    - Math.Max(0, StatusWidth() - StatusBar.ActualWidth)
            );
            if (StatusFits())
                return;
        }

        if (selected > 0 && Model.Page is WindowPage.Library or WindowPage.Torrents)
            SetStatus(
                CountLabel,
                Model.Text.Format("window", "selected", selected),
                Model.CountStatus
            );
        else
            ShowStatus(ContextStatus, false);
        if (StatusFits())
            return;

        ShowStatus(ContextStatus, false);
        ShowWork(overflow: true);
        if (StatusFits())
            return;

        TransferStatus.Spacing = 8;
        ShowStatus(TransferLabel, false);
        if (RestrictionStatus.Visibility == Visibility.Visible)
        {
            var compact = Model.IsSessionPaused
                ? Model.Text.Get("status", "paused")
                : Model.Text.Get("window", "alternative");
            SetStatus(
                RestrictionLabel,
                library ? Model.Text.Format("window", "torrent_status", compact) : compact,
                library
                    ? Model.Text.Format("window", "torrent_status", Model.RestrictionTip)
                    : Model.RestrictionTip
            );
        }
        if (StatusFits())
            return;

        foreach (
            var views in _workViews.Values.Where(views =>
                views.Inline.Visibility == Visibility.Visible
            )
        )
        {
            var excess = Math.Max(0, StatusWidth() - StatusBar.ActualWidth);
            views.Inline.MaxWidth = Math.Max(0, views.Inline.DesiredSize.Width - excess);
        }
        if (
            !work
            && ContextStatus.Visibility == Visibility.Collapsed
            && RestrictionStatus.Visibility == Visibility.Collapsed
        )
        {
            ShowStatus(ContextStatus, true);
            CountLabel.MaxWidth = StatusBar.ActualWidth;
        }
    }

    private static void ShowStatus(FrameworkElement control, bool visible) =>
        control.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private static void SetStatus(TextBlock label, string text, string? description = null)
    {
        if (label.Text != text)
            label.Text = text;
        description ??= text;
        if (AutomationProperties.GetName(label) != description)
            AutomationProperties.SetName(label, description);
        if (!Equals(ToolTipService.GetToolTip(label), description))
            ToolTipService.SetToolTip(label, description);
    }

    private void ReserveDigits(StackPanel panel, TextBlock label)
    {
        var text = new string(
            label.Text.Select(character => char.IsDigit(character) ? '8' : character).ToArray()
        );
        var icon = (FrameworkElement)panel.Children[0];
        icon.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        panel.Width = MeasureText(label, text) + panel.Spacing + icon.DesiredSize.Width;
    }

    private double MeasureText(TextBlock label, string text)
    {
        _measureProbe.Style = label.Style;
        _measureProbe.Text = text;
        _measureProbe.InvalidateMeasure();
        _measureProbe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return _measureProbe.DesiredSize.Width;
    }

    private bool StatusFits() => StatusWidth() <= StatusBar.ActualWidth;

    private double StatusWidth()
    {
        var unlimited = new Size(double.PositiveInfinity, double.PositiveInfinity);
        ContextStatus.Measure(unlimited);
        ActivityStatus.Measure(unlimited);
        TransferStatus.Measure(unlimited);
        var groups = new[]
        {
            ContextStatus.DesiredSize.Width,
            ActivityStatus.DesiredSize.Width,
            TransferStatus.DesiredSize.Width,
        }
            .Where(width => width > 0)
            .ToArray();
        return groups.Sum() + Math.Max(0, groups.Length - 1) * 16;
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
        var speedWidth = 192 * _uiSettings.TextScaleFactor + TitleSpeeds.ColumnSpacing;
        TitleSpeeds.Width = speedWidth;
        var available =
            Caption.ActualWidth
            - left
            - right
            - CaptionStart.ActualWidth
            - Menus.ActualWidth
            - SettingsButton.ActualWidth
            - LanguageButton.ActualWidth
            - ThemeButton.ActualWidth
            - SearchArea.Margin.Left
            - SearchArea.Margin.Right;
        TitleSpeeds.Visibility =
            Model.ShowsTitleSpeeds
            && available >= Search.MinWidth + speedWidth + TitleSpeeds.Margin.Left
                ? Visibility.Visible
                : Visibility.Collapsed;
        var searchSpace =
            available
            - (
                TitleSpeeds.Visibility == Visibility.Visible
                    ? speedWidth + TitleSpeeds.Margin.Left
                    : 0
            );
        Search.Width = Math.Clamp(searchSpace, Search.MinWidth, Search.MaxWidth);
        var searchStart =
            left + CaptionStart.ActualWidth + Menus.ActualWidth + SearchArea.Margin.Left;
        var offset = Math.Clamp(
            (Caption.ActualWidth - Search.Width) / 2 - searchStart,
            0,
            Math.Max(0, searchSpace - Search.Width)
        );
        if (Search.Margin.Left != offset)
            Search.Margin = new Thickness(offset, 0, 0, 0);
        QueueDragRegions();
    }

    private void QueueDragRegions()
    {
        if (_dragPending || _allowClose)
            return;
        _dragPending = DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                _dragPending = false;
                UpdateDragRegions();
            }
        );
    }

    private void UpdateDragRegions()
    {
        if (_allowClose || Root.XamlRoot is null || Caption.ActualHeight <= 0)
            return;
        var scale = Root.XamlRoot.RasterizationScale;
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
            Search,
            SettingsButton,
            LanguageButton,
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
        var regions = rectangles.ToArray();
        if (_dragRegions is not null && _dragRegions.SequenceEqual(regions))
            return;
        AppWindow.TitleBar.SetDragRectangles(regions);
        _dragRegions = regions;
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

    private void UpdateMinimum(double scale)
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
            return;
        var content =
            CaptionStart.ActualWidth
            + Menus.ActualWidth
            + SearchArea.Margin.Left
            + Search.MinWidth
            + SearchArea.Margin.Right
            + SettingsButton.Width
            + LanguageButton.Width
            + ThemeButton.Width;
        var frame = AppWindow.Size.Width - AppWindow.ClientSize.Width;
        var width =
            (int)
                Math.Ceiling(
                    Math.Max(720, content + LeftInset.Width.Value + RightInset.Width.Value) * scale
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
        ThemeIcon.Glyph = Model.IsDark ? Syno.Lucide.Lightbulb : Syno.Lucide.LightbulbOff;
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
        LanguageLabel.Text =
            Model.Text.Language == "zh"
                ? "中文"
                : Model.Text.Language.Split('-')[0].ToUpperInvariant();
        NameButton(LanguageButton, Model.Text.Get("settings", "language"));
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
        QueueStatus();
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
        CompletedOnColumn.DisplayName = Model.Text.Get("columns", "completed_on");
        Search.PlaceholderText = Model.Text.Get(
            Model.Page == WindowPage.Library ? "library" : "finding",
            Model.Page == WindowPage.Library ? "search" : "placeholder"
        );
        AutomationProperties.SetName(Search, Model.Text.Get("finding", "search"));
        Search.ItemsSource = Model.FindSuggestions(Search.Text);
        FiltersTitle.Text = Model.Text.Get("filters", "title");
        AutomationProperties.SetName(Filters, Model.Text.Get("filters", "title"));
        FiltersPane.RefreshText(Model.Text, "filters");
        AutomationProperties.SetName(Split, Model.Text.Get("inspector", "resize"));
        Torrents.Strings = Model.Text.Table;
        Root.FlowDirection = Model.Text.IsRightToLeft
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;
        RefreshPopups();
        _interaction?.RefreshText?.Invoke();
        Torrents.RefreshView();
        RefreshNavigation();
        UpdateChrome();
    }

    // A popup takes the app theme and reading direction, not the window's.
    private void RefreshPopups()
    {
        if (_interaction?.Dialog is { } dialog)
        {
            dialog.RequestedTheme = Root.ActualTheme;
            dialog.FlowDirection = Root.FlowDirection;
        }
        NoticeStack.RequestedTheme = Root.ActualTheme;
        NoticeStack.FlowDirection = Root.FlowDirection;
        WorkContent.RequestedTheme = Root.ActualTheme;
        WorkContent.FlowDirection = Root.FlowDirection;
    }
}
