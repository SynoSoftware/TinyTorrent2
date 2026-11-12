using Microsoft.UI;
using Microsoft.UI.Dispatching;
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

    // The text each ReserveDigits panel was last sized for, with every digit
    // as 8. QueueStatus clears it, because a text scale or language change
    // gives the same text another width.
    private readonly Dictionary<StackPanel, string> _reservedDigits = [];

    private void OnTextScaling(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            QueueStatus();
            UpdateChrome();
        });

    private bool _statusPending;

    // The content and width the status bar was last laid out for; null when
    // something the content does not hold has changed since.
    private (StatusContent Content, double Width)? _laidOutStatus;

    // The model's values that the status bar shows. LayoutStatus reads the
    // model only through this, so the bar is laid out again only when one of
    // these or its width changed. Words from the language catalogue are read
    // where they show, because a language change lays the bar out anyway.
    private readonly record struct StatusContent
    {
        internal WindowPage Page { get; init; }
        internal bool IsAbout { get; init; }
        internal bool IsRunning { get; init; }
        internal bool HasTransfers { get; init; }
        internal bool HasWork { get; init; }
        internal int SelectedCount { get; init; }
        internal string StatusGlyph { get; init; }
        internal string CountStatus { get; init; }
        internal string InformationStatus { get; init; }
        internal string DownloadStatus { get; init; }
        internal string UploadStatus { get; init; }
        internal string DownloadTip { get; init; }
        internal string UploadTip { get; init; }
        internal string DownloadSpeed { get; init; }
        internal string UploadSpeed { get; init; }
        internal string LifetimeDownloaded { get; init; }
        internal string LifetimeUploaded { get; init; }
        internal string MoveSpeed { get; init; }
        internal string MoveTip { get; init; }
        internal string CheckSpeed { get; init; }
        internal string CheckTip { get; init; }
        internal string EngineMemory { get; init; }
        internal string UiMemory { get; init; }
        internal string RestrictionStatus { get; init; }
        internal string RestrictionTip { get; init; }
        internal string FreeSpaceStatus { get; init; }
        internal string? Destination { get; init; }
        internal string ExternalIp { get; init; }
        internal string IncomingStatus { get; init; }
        internal string Incoming { get; init; }
        internal bool ShowsMemory { get; init; }
        internal bool HasRestriction { get; init; }
        internal bool HasCaps { get; init; }
        internal bool ShowsAlternativeLimits { get; init; }
        internal bool IsSessionPaused { get; init; }
        internal bool ShowsFreeSpace { get; init; }
        internal bool ShowsExternalIp { get; init; }
        internal bool HasUpdate { get; init; }
    }

    private StatusContent ReadStatus() =>
        new()
        {
            Page = Model.Page,
            IsAbout = Model.IsAbout,
            IsRunning = Model.IsRunning,
            HasTransfers = Model.Torrents.Count > 0 || Model.IsLoading,
            HasWork = Model.Work.Items.Count > 0,
            SelectedCount =
                Model.Page == WindowPage.Library
                    ? Model.Library.Selection.Items.Count
                    : Model.Selected.Count,
            StatusGlyph = Model.Library.StatusGlyph,
            CountStatus = Model.CountStatus,
            InformationStatus = Model.Library.InformationStatus,
            DownloadStatus = Model.DownloadStatus,
            UploadStatus = Model.UploadStatus,
            DownloadTip = Model.DownloadTip,
            UploadTip = Model.UploadTip,
            DownloadSpeed = Model.DownloadSpeed,
            UploadSpeed = Model.UploadSpeed,
            LifetimeDownloaded = Model.LifetimeDownloaded,
            LifetimeUploaded = Model.LifetimeUploaded,
            MoveSpeed = Model.MoveSpeed,
            MoveTip = Model.MoveTip,
            CheckSpeed = Model.CheckSpeed,
            CheckTip = Model.CheckTip,
            EngineMemory = Model.EngineMemory,
            UiMemory = Model.UiMemory,
            RestrictionStatus = Model.RestrictionStatus,
            RestrictionTip = Model.RestrictionTip,
            FreeSpaceStatus = Model.FreeSpaceStatus,
            Destination = Model.Settings.Destination.ConfirmedText,
            ExternalIp = Model.ExternalIp,
            IncomingStatus = Model.IncomingStatus,
            Incoming = Model.Incoming,
            ShowsMemory = Model.ShowsMemory,
            HasRestriction = Model.HasRestriction,
            HasCaps = Model.HasCaps,
            ShowsAlternativeLimits = Model.ShowsAlternativeLimits,
            IsSessionPaused = Model.IsSessionPaused,
            ShowsFreeSpace = Model.ShowsFreeSpace,
            ShowsExternalIp = Model.ShowsExternalIp,
            HasUpdate = Model.HasUpdate,
        };

    // Lays the status bar out after this turn, also when its content is
    // unchanged, for what the content does not hold: background work, focus
    // in the work list, the language and the text scale.
    private void QueueStatus()
    {
        _laidOutStatus = null;
        _reservedDigits.Clear();
        ObserveStatus();
    }

    // Lays the status bar out after this turn when its content changed.
    private void ObserveStatus()
    {
        if (_statusPending || _allowClose)
            return;
        _statusPending = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
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
        var status = (Content: ReadStatus(), Width: StatusBar.ActualWidth);
        if (_laidOutStatus is { } laidOut && laidOut == status)
            return;
        RefreshWork();
        try
        {
            LayoutStatus(status.Content);
            _laidOutStatus = status;
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

    private void LayoutStatus(StatusContent content)
    {
        var library = content.Page == WindowPage.Library;
        var about = content.IsAbout;
        var current = content.IsRunning;
        var transfers = content.HasTransfers;
        var work = content.HasWork;
        var selected = content.SelectedCount;
        foreach (var label in new[] { CountLabel, InformationLabel, RestrictionLabel })
            label.MaxWidth = double.PositiveInfinity;
        TransferStatus.Spacing = 16;
        ContextIcon.Glyph =
            about ? Syno.Segoe.Info
            : library ? content.StatusGlyph
            : content.Page == WindowPage.Settings ? Syno.Segoe.Settings
            : Syno.Segoe.List;
        SetStatus(CountLabel, content.CountStatus);
        SetStatus(InformationLabel, content.InformationStatus);
        var downloadTip = about
            ? Model.Text.Format("about", "downloaded_hint", content.LifetimeDownloaded)
            : content.DownloadTip;
        var uploadTip = about
            ? Model.Text.Format("about", "uploaded_hint", content.LifetimeUploaded)
            : content.UploadTip;
        SetStatus(
            DownloadLabel,
            about
                ? Model.Text.Format("about", "downloaded", content.LifetimeDownloaded)
                : content.DownloadStatus,
            downloadTip
        );
        SetStatus(
            UploadLabel,
            about
                ? Model.Text.Format("about", "uploaded", content.LifetimeUploaded)
                : content.UploadStatus,
            uploadTip
        );
        SetStatus(MoveLabel, content.MoveSpeed, content.MoveTip, MoveRate);
        SetStatus(CheckLabel, content.CheckSpeed, content.CheckTip);
        SetStatus(
            TransferLabel,
            about
                ? Model.Text.Get("settings", "lifetime_totals")
                : Model.Text.Get("window", "torrents")
        );
        SetStatus(MemoryCaption, Model.Text.Get("about", "memory"));
        SetStatus(
            EngineMemoryLabel,
            Model.Text.Format("about", "engine_memory", content.EngineMemory),
            Model.Text.Format("about", "engine_memory_hint", content.EngineMemory)
        );
        SetStatus(
            UiMemoryLabel,
            Model.Text.Format("about", "ui_memory", content.UiMemory),
            Model.Text.Format("about", "ui_memory_hint", content.UiMemory)
        );
        SetStatus(RestrictionLabel, content.RestrictionStatus, content.RestrictionTip);
        SetStatus(LimitsLabel, Model.Text.Get("window", "limits_on"));
        SetStatus(
            FreeSpaceLabel,
            content.FreeSpaceStatus,
            Model.Text.Format(
                "window",
                "space_path",
                content.Destination ?? string.Empty,
                content.FreeSpaceStatus
            )
        );
        SetStatus(ExternalIpLabel, content.ExternalIp);
        SetStatus(IncomingLabel, content.IncomingStatus, content.Incoming);
        SetStatus(
            UpdateLabel,
            Model.Text.Get("about", "available"),
            Model.Text.Get("about", "available_tip")
        );

        ShowStatus(ContextStatus, content.CountStatus.Length > 0);
        ShowStatus(ContextIcon, true);
        ShowStatus(
            InformationStatus,
            library && current && !work && content.InformationStatus.Length > 0
        );
        ShowStatus(MemoryStatus, content.ShowsMemory);
        ShowStatus(DownloadRate, about || transfers);
        ShowStatus(UploadRate, about || transfers);
        ShowStatus(MoveRate, !about && current && content.MoveSpeed.Length > 0);
        ShowStatus(CheckRate, !about && current && content.CheckSpeed.Length > 0);
        ShowStatus(RestrictionStatus, !about && current && content.HasRestriction);
        ShowStatus(
            TransferLabel,
            about || library && (transfers || current && content.HasRestriction)
        );
        ShowStatus(LimitsLabel, false);
        ShowStatus(FreeSpaceStatus, !about && content.ShowsFreeSpace);
        ShowStatus(ExternalIpStatus, !about && current && content.ShowsExternalIp);
        ShowStatus(Incoming, current && content.Page == WindowPage.Torrents);
        ShowStatus(UpdateAvailable, !about && content.HasUpdate);
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
        ReserveDigits(MoveRate, MoveLabel);
        ReserveDigits(CheckRate, CheckLabel);
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
            about ? content.LifetimeDownloaded : content.DownloadSpeed,
            downloadTip
        );
        SetStatus(UploadLabel, about ? content.LifetimeUploaded : content.UploadSpeed, uploadTip);
        ShowStatus(
            LimitsLabel,
            !about && transfers && content.HasCaps && !content.ShowsAlternativeLimits
        );
        ReserveDigits(DownloadRate, DownloadLabel);
        ReserveDigits(UploadRate, UploadLabel);
        if (StatusFits())
            return;

        ShowStatus(DownloadRate, false);
        ShowStatus(UploadRate, false);
        ShowStatus(MoveRate, false);
        ShowStatus(CheckRate, false);
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

        if (selected > 0 && Navigation.IsWorkspace(content.Page))
            SetStatus(
                CountLabel,
                Model.Text.Format("window", "selected", selected),
                content.CountStatus
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
            var compact = content.IsSessionPaused
                ? Model.Text.Get("status", "paused")
                : Model.Text.Get("window", "alternative");
            SetStatus(
                RestrictionLabel,
                library ? Model.Text.Format("window", "torrent_status", compact) : compact,
                library
                    ? Model.Text.Format("window", "torrent_status", content.RestrictionTip)
                    : content.RestrictionTip
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

    // A label without a description shows its own text in a tooltip only while
    // it is trimmed. One label can be set both ways, so each call switches.
    private static void SetStatus(TextBlock label, string text, string? description = null, FrameworkElement? tip = null)
    {
        if (label.Text != text)
            label.Text = text;
        var name = description ?? text;
        if (AutomationProperties.GetName(label) != name)
            AutomationProperties.SetName(label, name);
        Syno.TinyTorrent.Controls.Trimming.SetShowsToolTip(label, description is null);
        if (description is null)
        {
            return;
        }
        tip ??= label;
        if (!Equals(ToolTipService.GetToolTip(tip), description))
            ToolTipService.SetToolTip(tip, description);
    }

    private void ReserveDigits(StackPanel panel, TextBlock label)
    {
        var text = new string(
            label.Text.Select(character => char.IsDigit(character) ? '8' : character).ToArray()
        );
        if (_reservedDigits.TryGetValue(panel, out var reserved) && reserved == text)
        {
            return;
        }
        var icon = (FrameworkElement)panel.Children[0];
        icon.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        panel.Width = MeasureText(label, text) + panel.Spacing + icon.DesiredSize.Width;
        _reservedDigits[panel] = text;
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
            WorkspacePicker,
            Menus,
            Search,
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

    // The smallest window in DIPs; the title bar's content can need more width.
    private const int MinimumWidth = 720;
    private const int MinimumHeight = 560;

    private void UpdateMinimum(double scale)
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
            return;
        var minimum = MinimumSize(scale);
        if (presenter.PreferredMinimumWidth != minimum.Width)
            presenter.PreferredMinimumWidth = minimum.Width;
        if (presenter.PreferredMinimumHeight != minimum.Height)
            presenter.PreferredMinimumHeight = minimum.Height;
        if (Model.Page == WindowPage.Settings && AppWindow.Size.Width < minimum.Width)
            AppWindow.Resize(new SizeInt32(minimum.Width, AppWindow.Size.Height));
    }

    // The smallest window, in physical pixels and with its frame, that shows
    // the whole title bar.
    private SizeInt32 MinimumSize(double scale)
    {
        var content =
            CaptionStart.ActualWidth
            + Menus.ActualWidth
            + SearchArea.Margin.Left
            + Search.MinWidth
            + SearchArea.Margin.Right
            + LanguageButton.Width
            + ThemeButton.Width;
        var frame = AppWindow.Size.Width - AppWindow.ClientSize.Width;
        var width =
            (int)
                Math.Ceiling(
                    Math.Max(MinimumWidth, content + LeftInset.Width.Value + RightInset.Width.Value)
                        * scale
                ) + frame;
        var height = (int)Math.Ceiling(MinimumHeight * scale);
        return new SizeInt32(width, height);
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
        ThemeIcon.Glyph = Model.IsDark ? Syno.Segoe.Lightbulb : Syno.Segoe.LightbulbOff;
        NameButton(ThemeButton, Model.Text.Get("chrome", Model.IsDark ? "light" : "dark"));
    }

    private static void NameButton(Button button, string text)
    {
        if (AutomationProperties.GetName(button) != text)
        {
            AutomationProperties.SetName(button, text);
        }
        if (!Equals(ToolTipService.GetToolTip(button), text))
        {
            ToolTipService.SetToolTip(button, text);
        }
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
        NameButton(StatusMore, Model.Text.Get("notifications", "silence_location"));
        NameButton(
            BackButton,
            Model.Text.ShortcutTip(Model.Text.Get("menus", "back"), ShortcutText(Back))
        );
        RefreshMenus();
        QueueStatus();
        AutomationProperties.SetName(Torrents, Model.Text.Get("window", "torrents"));
        NameColumn.DisplayName = Model.Text.Get("columns", "name");
        SizeColumn.DisplayName = Model.Text.Get("columns", "size");
        ProgressColumn.DisplayName = Model.Text.Get("columns", "progress");
        StatusColumn.DisplayName = Model.Text.Get("columns", "status");
        DownColumn.DisplayName = Model.Text.Get("columns", "down");
        DownColumn.ShortName = Model.Text.Get("columns", "down_short");
        UpColumn.DisplayName = Model.Text.Get("columns", "up");
        UpColumn.ShortName = Model.Text.Get("columns", "up_short");
        LimitColumn.DisplayName = Model.Text.Get("columns", "limit");
        QueueColumn.DisplayName = Model.Text.Get("columns", "queue");
        QueueColumn.ShortName = Model.Text.Get("columns", "queue_short");
        EtaColumn.DisplayName = Model.Text.Get("columns", "eta");
        EtaColumn.ShortName = Model.Text.Get("columns", "eta_short");
        RatioColumn.DisplayName = Model.Text.Get("columns", "ratio");
        SeedsColumn.DisplayName = Model.Text.Get("columns", "seeds");
        PeersColumn.DisplayName = Model.Text.Get("columns", "peers");
        AddedColumn.DisplayName = Model.Text.Get("columns", "added");
        CompletedOnColumn.DisplayName = Model.Text.Get("columns", "completed_on");
        Search.PlaceholderText = SearchPlaceholder;
        AutomationProperties.SetName(Search, Model.Text.Get("finding", "search"));
        Search.ItemsSource = Model.Page == WindowPage.Library ? null : Model.FindSuggestions(Search.Text);
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
        ToastStack.RequestedTheme = Root.ActualTheme;
        ToastStack.FlowDirection = Root.FlowDirection;
        WorkContent.RequestedTheme = Root.ActualTheme;
        WorkContent.FlowDirection = Root.FlowDirection;
    }
}
