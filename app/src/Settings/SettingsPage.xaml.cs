using System.ComponentModel;
using Microsoft.UI.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shell;
using Windows.System;
using Windows.UI.ViewManagement;
using ProviderOption = Syno.TinyTorrent.Library.ProviderOption;
using SubtitleSettings = Syno.TinyTorrent.Subtitles.Settings;

namespace Syno.TinyTorrent.Settings;

public sealed partial class SettingsPage : UserControl
{
    private readonly HashSet<TextBox> _composing = [];
    private readonly HashSet<TextBox> _editors = [];
    private bool _refreshing;
    private IReadOnlyList<ComboBoxItem>? _languageItems;
    // The text that names each language item. The box moves the selected
    // item's content into its own presenter, so the item's Content is null
    // while it is selected.
    private readonly List<TextBlock> _languageNames = [];
    private bool _showAdvanced;
    private ThemePicker? _themes;
    private WindowsIntegration? _integration;
    private Scheduler? _scheduler;
    private SpeedLimits? _speedLimits;
    private SpeedLimits? _scheduleLimits;
    private Syno.TinyTorrent.Subtitles.Automatic? _automatic;
    private readonly UISettings _display = new();
    private ThemeSettings? _themeSettings;
    private readonly Motion _motion = new();
    private readonly Motion _pages = new();
    private readonly Dictionary<NavigationViewItem, double> _positions = [];
    private NavigationViewItem? _category;
    private double _indexPosition;
    public Editor Model { get; }
    public MainViewModel Main { get; }
    public event EventHandler<Setting>? FolderRequested;
    public event EventHandler? ProxyRequested;
    internal event EventHandler<SubtitleSupplier>? SupplierRequested;
    internal event EventHandler<ProviderSetup>? KeyRequested;

    /// <summary>Automatic downloading needs the selected provider's data installed.</summary>
    internal event EventHandler? AutomaticDataRequested;
    internal event EventHandler<IDataset>? UpdateRequested;
    internal event EventHandler<IDataset>? DeleteRequested;
    public event EventHandler? ConnectionRequested;
    internal event EventHandler? ResetRequested;
    internal event EventHandler? LayoutChanged;

    public SettingsPage(MainViewModel main)
    {
        Main = main;
        Model = main.Settings;
        InitializeComponent();
        Svg.Fit(Logo);
        Categories.ItemInvoked += OnCategoryInvoked;
        // Keep the overhanging corner marks off the navigation pane and caption.
        Root.SizeChanged += Motion.Clip;
        BodyContent.SizeChanged += Motion.Clip;
        PageHost.SizeChanged += Motion.Clip;
        BodyContent.LayoutUpdated += (_, _) => PositionOutline();
        _outlineTimer.Tick += (_, _) => ClearOutline();
        _pages.Show(PageContent);
        GotFocus += OnFieldFocus;
        PreviewKeyDown += OnFieldKey;
        LostFocus += OnFieldDeparture;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        RefreshText();
        UpdateDisclosure();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        Model.TextChanged += OnText;
        Model.PropertyChanged += OnModel;
        Main.PropertyChanged += OnMain;
        Main.Library.PropertyChanged += OnProviders;
        RefreshSubtitles();
        _display.TextScaleFactorChanged += OnTextScale;
        _themeSettings = ThemeSettings.CreateForWindowId(
            XamlRoot.ContentIslandEnvironment.AppWindowId
        );
        _themeSettings.Changed += OnThemeChanged;
        RefreshIcons();
        RefreshText();
        _ = Model.RefreshAdapters();
        _ = Model.ObserveRegistration();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args) => Release();

    internal void Release()
    {
        _automatic?.Attach(null);
        if (SubtitlesContent?.Content is SubtitleSettings subtitles)
        {
            subtitles.SetVisible(false);
            subtitles.Attach(null);
        }
        Model.HidePlugins();
        ClearOutline();
        _motion.Stop();
        _pages.Stop();
        Model.TextChanged -= OnText;
        Model.PropertyChanged -= OnModel;
        Main.PropertyChanged -= OnMain;
        Main.Library.PropertyChanged -= OnProviders;
        _display.TextScaleFactorChanged -= OnTextScale;
        if (_themeSettings is { } theme)
        {
            theme.Changed -= OnThemeChanged;
            _themeSettings = null;
        }
    }

    private void OnThemeChanged(ThemeSettings sender, object args) =>
        DispatcherQueue.TryEnqueue(RefreshIcons);

    private void RefreshIcons()
    {
        if (_themeSettings is not { } theme)
            return;
        var size = (double)Categories.Resources["NavigationViewItemOnLeftIconBoxHeight"];
        foreach (
            var (item, glyph, asset) in new[]
            {
                (HomeCategory, Lucide.House, "home"),
                (GeneralCategory, Lucide.Settings, "general"),
                (TransfersCategory, Lucide.ArrowDownUp, "transfers"),
                (LimitsCategory, Lucide.SlidersHorizontal, "limits"),
                (ScheduleCategory, Lucide.CalendarClock, "schedule"),
                (AppearanceCategory, Lucide.Palette, "appearance"),
                (NetworkCategory, Lucide.Network, "network"),
                (PluginsCategory, Lucide.Puzzle, "library"),
                (AdvancedCategory, Lucide.Wrench, "advanced"),
            }
        )
        {
            if (theme.HighContrast)
            {
                item.Icon = new FontIcon
                {
                    FontFamily = Lucide.Font,
                    Glyph = glyph,
                    FontSize = size,
                };
            }
            else
            {
                var icon = new ImageIcon
                {
                    Source = new SvgImageSource(new Uri($"ms-appx:///Assets/Settings/{asset}.svg")),
                    Width = size,
                    Height = size,
                };
                Svg.Fit(icon);
                item.Icon = icon;
            }
            AutomationProperties.SetAccessibilityView(item.Icon, AccessibilityView.Raw);
        }
    }

    private void OnText(object? sender, EventArgs args) => RefreshText();

    private void OnAutomaticDownload(object sender, RoutedEventArgs args)
    {
        var requirement = Main.Library.ChooseAutomaticDownload(AutomaticDownload.IsOn);
        if (requirement == ProviderRequirement.Data)
            AutomaticDataRequested?.Invoke(this, EventArgs.Empty);
        else if (requirement == ProviderRequirement.Key && Main.Library.ChosenProvider is { } option)
            KeyRequested?.Invoke(this, new(option, true));
    }

    private void OnProviderKey(object sender, RoutedEventArgs args)
    {
        if (Main.Library.Option is { Access: Access.Key } option)
            KeyRequested?.Invoke(this, new(option, false));
    }

    private void OnProviderInformation(object sender, RoutedEventArgs args)
    {
        if (Main.Library.Option is { } option)
            Disclosure
                .Create(
                    Model.Text,
                    option.Name,
                    Model.Text.Format(option.ProviderId, "consent", option.Name),
                    option.Privacy,
                    option.Terms
                )
                .ShowAt(ProviderInformation);
    }

    private void OnResetAll(object sender, RoutedEventArgs args) =>
        ResetRequested?.Invoke(this, EventArgs.Empty);

    private async void OnProtectionReset(object sender, RoutedEventArgs args) =>
        await Main.ResetProtection();

    private void OnMain(object? sender, PropertyChangedEventArgs args)
    {
        if (
            args.PropertyName is nameof(MainViewModel.Page) or nameof(MainViewModel.Subtitles) or ""
        )
            RefreshSubtitles();
        if (
            args.PropertyName
            is nameof(MainViewModel.CurrentStatus)
                or nameof(MainViewModel.Page)
                or nameof(MainViewModel.Subtitles)
                or nameof(MainViewModel.FreeSpaceStatus)
                or nameof(MainViewModel.ShowsFreeSpace)
                or nameof(MainViewModel.SessionTotals)
                or nameof(MainViewModel.LifetimeTotals)
                or nameof(MainViewModel.Torrents)
                or nameof(MainViewModel.LimitsSummary)
                or nameof(MainViewModel.ScheduleSummary)
                or nameof(MainViewModel.LimitsNow)
                or nameof(MainViewModel.IncomingStatus)
                or ""
        )
            QueueHome();
    }

    private void OnProviders(object? sender, PropertyChangedEventArgs args)
    {
        if (
            args.PropertyName
            is nameof(Main.Library.Status)
                or nameof(Main.Library.IsAutomaticDownloadEnabled)
                or nameof(Main.Library.Providers)
                or ""
        )
            QueueHome();
        if (args.PropertyName is nameof(Main.Library.Providers) or "")
            ShowImdb();
    }

    // The row has nothing to manage until Library attaches.
    private void ShowImdb() =>
        ImdbRow?.Show(Main.Library.DataProvider is null ? null : Main.Library, Model.Text);

    private void OnTextScale(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            AlignCategory();
            UpdateHomeLayout();
        });

    private void OnModel(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(Editor.SubtitleCoverage) or "")
            RefreshSubtitleCoverage();
        QueueHome();
    }

    // Built in code, because a template cannot give each item's container its
    // own AutomationId.
    public IReadOnlyList<ComboBoxItem> LanguageItems =>
        _languageItems ??=
        [
            .. Strings.Languages.Select(language =>
            {
                var name = new TextBlock { Text = Model.Text.LanguageChoice(language) };
                _languageNames.Add(name);
                var flag = new TextBlock
                {
                    Text = Flags.Glyph(Strings.FlagRegion(language)),
                    FontFamily = Flags.Font,
                };
                AutomationProperties.SetAccessibilityView(flag, AccessibilityView.Raw);
                var item = new ComboBoxItem
                {
                    Tag = language.Name,
                    Content = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        Children = { flag, name },
                    },
                };
                AutomationProperties.SetName(item, name.Text);
                AutomationProperties.SetAutomationId(
                    item,
                    language.Name switch
                    {
                        "en" => "EnglishLanguage",
                        "es" => "SpanishLanguage",
                        _ => language.Name + "Language",
                    }
                );
                return item;
            }),
        ];

    // The language items name every other language in the window's language.
    private void ShowLanguageNames()
    {
        var items = LanguageItems;
        for (var i = 0; i < items.Count; i++)
        {
            var name = Model.Text.LanguageChoice(Strings.Languages[i]);
            _languageNames[i].Text = name;
            AutomationProperties.SetName(items[i], name);
        }
        // The bindings build the items before the Appearance panel loads the
        // box, which happens when the person first opens that panel.
        if (Languages is null)
            return;
        // The box keeps the text its item had when it was selected.
        var selected = Languages.SelectedItem;
        Languages.SelectedItem = null;
        Languages.SelectedItem = selected;
    }

    internal void RefreshText()
    {
        ShowImdb();
        CityRow?.RefreshText();
        Bindings.Update();
        ShowLanguageNames();
        RefreshSubtitles();
        RefreshAdvancedMode();
        // A ComboBox keeps the text its item had when it was selected, so each
        // choice is selected again after its item texts change.
        foreach (
            var choice in CategoryPanels
                .SelectMany(panel => Elements(panel))
                .OfType<SettingsRow>()
                .Where(row => row.Setting is not null)
                .SelectMany(row => new[] { row.Content, row.UnitSelector })
                .OfType<ComboBox>()
        )
        {
            var item = choice.SelectedItem;
            choice.SelectedItem = null;
            choice.SelectedItem = item;
        }
        RefreshHome();
        (SubtitlesContent?.Content as SubtitleSettings)?.RefreshText();
        _automatic?.Refresh();
        AlignCategory();
    }

    private async void OnCategory(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args
    )
    {
        if (_refreshing || args.SelectedItem is not NavigationViewItem item)
            return;
        var index = Array.IndexOf(CategoryItems, item);
        var target = new SettingTarget(index < 0 ? null : (SettingsCategory)index);
        if (Main.Navigation.Current != WindowPage.Connection && item == (_category ?? HomeCategory))
            return;
        _refreshing = true;
        Categories.SelectedItem =
            Main.Navigation.Current == WindowPage.Connection
                ? LimitsCategory
                : _category ?? HomeCategory;
        _refreshing = false;
        if (await Main.Navigation.SelectCategory(target.Category))
            FocusTarget(target);
    }

    private async void OnCategoryInvoked(
        NavigationView sender,
        NavigationViewItemInvokedEventArgs args
    )
    {
        if (args.InvokedItemContainer == AdvancedMode)
        {
            ShowAdvanced(!_showAdvanced);
            return;
        }
        if (args.InvokedItemContainer == ReturnWorkspace)
        {
            Main.ReturnWorkspace.Execute(null);
            return;
        }
        if (
            Main.Navigation.Current == WindowPage.Connection
            && args.InvokedItemContainer == LimitsCategory
            && await Main.Navigation.SelectCategory(SettingsCategory.Limits)
        )
            FocusTarget(new(SettingsCategory.Limits));
    }

    internal void ShowConnection(ConnectionPage? page, bool animate = true)
    {
        ClearOutline();
        if (page is not null)
            ChildContent.Content = page;
        FrameworkElement content = page is null ? PageContent : ChildContent;
        if (!ReferenceEquals(_pages.Current, content))
            _motion.Stop();
        if (animate)
            _pages.Show(content, forward: page is not null);
        else
            _pages.Replace(content);
        _refreshing = true;
        Categories.SelectedItem = page is null ? _category ?? HomeCategory : LimitsCategory;
        _refreshing = false;
        RefreshSubtitles();
    }

    private void ShowCategory(bool animate = true)
    {
        var next = ReferenceEquals(Categories.SelectedItem, HomeCategory)
            ? null
            : Categories.SelectedItem as NavigationViewItem;
        if (
            _category == next
            && (
                _category is null
                    ? IndexContent is not null
                    : CategoryPanels.Any(panel => panel.Name == (string)_category.Tag)
            )
        )
        {
            RefreshSubtitles();
            RefreshHome();
            return;
        }
        var previousIndex = _category is null ? -1 : Categories.MenuItems.IndexOf(_category);
        if (_category is { } previous)
            _positions[previous] = Body.VerticalOffset;
        else
            _indexPosition = Body.VerticalOffset;
        _category = next;
        var nextIndex = _category is null ? -1 : Categories.MenuItems.IndexOf(_category);
        if (animate && !_pages.IsActive)
            _motion.Enter(PageContent, forward: nextIndex >= previousIndex, update: UpdateContent);
        else
        {
            _motion.Stop();
            UpdateContent();
        }

        void UpdateContent()
        {
            var content = LoadCategory(out var loaded);
            if (loaded)
            {
                RefreshText();
                UpdateDisclosure();
            }
            else
            {
                RefreshSubtitles();
                RefreshHome();
            }
            // Only the active category contributes to the restored scroll extent.
            foreach (var panel in CategoryPanels.Append(IndexContent).OfType<FrameworkElement>())
                panel.Visibility = ReferenceEquals(panel, content)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            UpdateLayout();
            RefreshSubtitles();
            AlignCategory();
            var position = _category is { } item
                ? _positions.GetValueOrDefault(item)
                : _indexPosition;
            Body.ChangeView(null, position, null, true);
        }
    }

    private IEnumerable<FrameworkElement> CategoryPanels =>
        new FrameworkElement?[]
        {
            General,
            Transfers,
            Network,
            Limits,
            Appearance,
            Advanced,
            Schedule,
            Plugins,
        }.OfType<FrameworkElement>();

    private FrameworkElement LoadCategory(out bool loaded)
    {
        var name = _category is null ? nameof(IndexContent) : (string)_category.Tag;
        var existing = _category is null
            ? IndexContent
            : CategoryPanels.FirstOrDefault(panel => panel.Name == name);
        loaded = existing is null;
        if (existing is not null)
            return existing;
        var content = (FrameworkElement)FindName(name);
        if (_category is null)
            InitializeHome();
        else if (_category == GeneralCategory)
            IntegrationContent.Content = _integration = new WindowsIntegration(Model);
        else if (_category == AppearanceCategory)
            ThemeContent.Content = _themes = new ThemePicker(Model);
        else if (_category == LimitsCategory)
        {
            _speedLimits = new SpeedLimits(Main);
            _speedLimits.ConnectionRequested += OnConnection;
            SpeedContent.Content = _speedLimits;
            AddLink(
                RelatedLinks,
                "schedule",
                () => Main.ScheduleSummary,
                new(SettingsCategory.Schedule, "weekly_schedule"),
                Syno.Lucide.CalendarClock
            );
            AddLink(
                RelatedLinks,
                "queue",
                () => Model.QueueSummary,
                new(SettingsCategory.Transfers, "queue"),
                Syno.Lucide.ListOrdered
            );
        }
        else if (_category == ScheduleCategory)
        {
            ScheduleContent.Content = _scheduler = new Scheduler(
                Model.Schedule,
                new LimitSelector(Main)
            );
            _scheduleLimits = new SpeedLimits(Main, inline: true);
            _scheduleLimits.ConnectionRequested += OnConnection;
            ScheduleSpeedContent.Content = _scheduleLimits;
        }
        else if (_category == PluginsCategory)
        {
            foreach (var row in new[] { ImdbRow, CityRow })
            {
                row.DownloadRequested += (_, dataset) => _ = Main.DownloadData(dataset);
                row.UpdateRequested += (_, dataset) => UpdateRequested?.Invoke(this, dataset);
                row.DeleteRequested += (_, dataset) => DeleteRequested?.Invoke(this, dataset);
            }
            CityRow.Show(Main.Geolocation, Model.Text);
            var subtitles = new SubtitleSettings(
                Model.Text,
                Main.Features.RetrySubtitles,
                ReportSubtitles
            );
            subtitles.SupplierRequested += (_, supplier) =>
                SupplierRequested?.Invoke(this, supplier);
            SubtitlesContent.Content = subtitles;
        }
        return content;
    }

    private void ReportSubtitles(Exception error, string title) =>
        Main.Notifications.Report(error, title, null, null);

    internal void ShowAdvanced(bool show)
    {
        if (_showAdvanced == show)
            return;
        _showAdvanced = show;
        if (!show)
            _ = Model.PrepareLeave();
        RefreshAdvancedMode();
        UpdateDisclosure();
    }

    private void RefreshAdvancedMode()
    {
        AdvancedModeIcon.Glyph = _showAdvanced ? Lucide.SquareCheck : Lucide.Square;
        AutomationProperties.SetItemStatus(
            AdvancedMode,
            _showAdvanced ? Model.OnText : Model.OffText
        );
    }

    private void UpdateDisclosure()
    {
        AdvancedCategory.Visibility = _showAdvanced ? Visibility.Visible : Visibility.Collapsed;
        if (DatabaseSummary is not null)
        {
            DatabaseSummary.Visibility = _showAdvanced ? Visibility.Visible : Visibility.Collapsed;
            if (!_showAdvanced)
                DatabaseSummary.IsExpanded = false;
            else
                RefreshSubtitles();
        }
        foreach (var panel in CategoryPanels)
        foreach (var element in Elements(panel))
            if (element is SettingsRow { IsAdvanced: true } or SettingsSection { IsAdvanced: true })
                element.Visibility = _showAdvanced ? Visibility.Visible : Visibility.Collapsed;
        if (!_showAdvanced && ReferenceEquals(Categories.SelectedItem, AdvancedCategory))
            _ = Main.Navigation.SelectCategory(Main.SettingsHome);
        RefreshHome();
        AlignCategory();
    }

    private void AlignCategory()
    {
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        if (_category is null)
            return;
        var panel = CategoryPanels.First(panel => panel.Name == (string)_category.Tag);
        foreach (var section in Elements(panel, visible: true).OfType<SettingsSection>())
        {
            var rows = Elements(section, visible: true).OfType<SettingsRow>().ToArray();
            var width = 0d;
            foreach (
                var unit in rows.Select(row => row.Unit).Where(unit => unit.Length > 0).Distinct()
            )
            {
                var label = new TextBlock
                {
                    Text = unit,
                    Style = (Style)Application.Current.Resources["TinyTorrentBodyTextStyle"],
                };
                label.Measure(
                    new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity)
                );
                width = Math.Max(width, label.DesiredSize.Width);
            }
            foreach (var row in rows)
                row.Align(width);
        }
        if (_category == PluginsCategory)
            ActionButton.Align(Elements(panel).OfType<ActionButton>());
    }

    private static IEnumerable<FrameworkElement> Elements(
        FrameworkElement element,
        bool visible = false
    )
    {
        if (visible && element.Visibility != Visibility.Visible)
            yield break;
        yield return element;
        var children = element switch
        {
            Panel panel => panel.Children.OfType<FrameworkElement>(),
            Border border => new[] { border.Child }.OfType<FrameworkElement>(),
            SettingsRow row => new[]
            {
                row.Content,
                row.Detail,
                row.UnitSelector,
            }.OfType<FrameworkElement>(),
            ContentControl control => new[] { control.Content }.OfType<FrameworkElement>(),
            UserControl control => new[] { control.Content }.OfType<FrameworkElement>(),
            _ => [],
        };
        foreach (var child in children)
        foreach (var descendant in Elements(child, visible))
            yield return descendant;
    }

    private void OnConnection(object? sender, EventArgs args) =>
        ConnectionRequested?.Invoke(this, EventArgs.Empty);

    private SpeedLimits? CurrentProfiles =>
        _category == ScheduleCategory ? _scheduleLimits : _speedLimits;

    internal void FocusConnection() => CurrentProfiles?.FocusConnection();

    // In SettingsCategory order.
    private NavigationViewItem[] CategoryItems =>
        [
            GeneralCategory,
            TransfersCategory,
            NetworkCategory,
            LimitsCategory,
            AppearanceCategory,
            AdvancedCategory,
            ScheduleCategory,
            PluginsCategory,
        ];

    public static bool IsHome(SettingsCategory? category) => category is null;

    public static bool IsCategory(SettingsCategory? selected, SettingsCategory category) => selected == category;

    public static bool IsSelected(string selected, string value) => selected == value;

    internal SettingsCategory? Category
    {
        get
        {
            var index = Array.IndexOf(CategoryItems, Categories.SelectedItem);
            return index >= 0 ? (SettingsCategory)index : null;
        }
    }

    internal async Task Open(SettingTarget target)
    {
        if (await Main.Navigation.Navigate(target))
            FocusTarget(target);
    }

    internal void Navigate(SettingTarget target, bool animate = true)
    {
        ClearOutline();
        if (target.Category is not { } category)
        {
            _refreshing = true;
            Categories.SelectedItem = HomeCategory;
            _refreshing = false;
            ShowCategory(animate);
            return;
        }
        if (category == SettingsCategory.Advanced)
            ShowAdvanced(true);
        _refreshing = true;
        Categories.SelectedItem = CategoryItems[(int)category];
        _refreshing = false;
        ShowCategory(animate);
    }

    private void FocusTarget(SettingTarget target)
    {
        Reveal(target);
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (
                    Main.Navigation.Current != WindowPage.Settings
                    || Main.Navigation.Settings != target
                )
                    return;
                UpdateLayout();
                if (target.Category is null || target.Name is null)
                {
                    PageHeader.FocusHeading(FocusState.Programmatic);
                    return;
                }
                if (FindControl(target.Name) is { } control)
                    ShowDestination(control);
            }
        );
    }

    private void Reveal(SettingTarget target)
    {
        if (target.Name is not { } name || FindControl(name) is not { } control)
            return;
        var groups = CategoryPanels.SelectMany(panel => Elements(panel)).ToArray();
        foreach (var element in groups)
        {
            if (!Elements(element).Contains(control))
                continue;
            if (element is SettingsSection section)
            {
                if (section.IsAdvanced)
                    ShowAdvanced(true);
                section.IsExpanded = true;
            }
            else if (element is SettingsRow { IsAdvanced: true })
                ShowAdvanced(true);
            else if (element is Expander expander)
                expander.IsExpanded = true;
        }
    }

    private Control? FindControl(string settingName) =>
        settingName switch
        {
            "patterns" when !Model.Exclude.IsOn => FindControl("exclude"),
            "queue" => QueueSection,
            "weekly_schedule" => _scheduler?.Map,
            "add_period" when _scheduler?.Model.FollowsSchedule != true => _scheduler?.Selector,
            "add_period" => _scheduler?.Add,
            "seeding" => SeedingSection,
            "adding" => AddingSection,
            "download_locations" => DownloadsSection,
            "windows_integration" => _integration?.Section,
            "notifications" => NotificationsSection,
            "power" => PowerSection,
            "dialogs" => ConfirmationsSection,
            "startup_closing" => LifecycleSection,
            "watched_folder" => WatchedSection,
            "peer_connections" => PeersSection,
            "peer_discovery" => DiscoverySection,
            "incoming_connections" => IncomingSection,
            "connection_encryption" => EncryptionSection,
            "bandwidth_accounting" => AccountingSection,
            "start_signin" => _integration?.Field("start_signin"),
            "limit_mode" => _category == ScheduleCategory
                ? _scheduler?.Selector
                : _speedLimits?.Selector,
            "download_limit"
            or "upload_limit"
            or "alternative_download_limit"
            or "alternative_upload_limit" => CurrentProfiles?.Field(settingName),
            "open_defaults" => _integration?.Field("open_defaults"),
            "settings_index" => IndexSwitch,
            "network_interface" => Adapters,
            "proxy" => ProxyEdit,
            "connection_setup" => CurrentProfiles?.Field("connection_setup"),
            "encryption" => EncryptionChoices,
            "language" => Languages,
            "theme" => _themes?.Choices,
            "video_provider" => ProviderChoice,
            "video_information" => AutomaticDownload,
            "subtitle_automatic"
            or "subtitle_extras"
            or "subtitle_supplier"
            or "subtitle_languages"
            or "subtitle_batch"
            or "subtitle_scan" => (SubtitlesContent?.Content as SubtitleSettings)?.Field(
                settingName
            ),
            // The first control in the setting's row, which is its editor.
            _ => CategoryPanels
                .SelectMany(panel => Elements(panel))
                .OfType<SettingsRow>()
                .Where(row => row.Setting?.Name == settingName)
                .SelectMany(row => Elements(row).Skip(1))
                .OfType<Control>()
                .FirstOrDefault(),
        };

    private string SlowDescription(bool enabled, string key) =>
        Model.Text.Get("settings", enabled ? key : "enable_slow");

    private void OnIndexSwitch(object sender, RoutedEventArgs args) =>
        Main.ShowsSettingsIndex = IndexSwitch.IsOn;

    private async void OnEncryption(object sender, SelectionChangedEventArgs args)
    {
        if (EncryptionChoices.SelectedItem is FrameworkElement { Tag: string value })
            await Model.Encryption.Select(value);
    }

    private async void OnAdapter(object sender, SelectionChangedEventArgs args)
    {
        if (Adapters.SelectedItem is AdapterChoice choice)
            await Model.Adapter.Select(choice.AdapterId);
    }

    private void OnProvider(object sender, SelectionChangedEventArgs args) =>
        Main.Library.ChooseProvider(ProviderChoice.SelectedIndex);

    private void OnLanguage(object sender, SelectionChangedEventArgs args)
    {
        // The binding selects the chosen language, which is no new choice.
        if (Languages.SelectedValue is string language && language != Model.ChosenLanguage)
            Model.SelectLanguage(language);
    }

    private void OnFolder(object sender, RoutedEventArgs args)
    {
        if (RowOf((DependencyObject)sender)?.Setting is { } setting)
            FolderRequested?.Invoke(this, setting);
    }

    private void OnProxy(object sender, RoutedEventArgs args) =>
        ProxyRequested?.Invoke(this, EventArgs.Empty);

    private static SettingsRow? RowOf(DependencyObject element)
    {
        for (
            var current = element;
            current is not null;
            current = VisualTreeHelper.GetParent(current)
        )
            if (current is SettingsRow row)
                return row;
        return null;
    }

    // Text fields of every setting row, including the speed limit rows, save
    // through the handlers below. Composition starts only in a focused field,
    // so a field is watched from its first focus.
    private void OnFieldFocus(object sender, RoutedEventArgs args)
    {
        if (
            args.OriginalSource is TextBox editor
            && RowOf(editor)?.Setting is not null
            && _editors.Add(editor)
        )
        {
            editor.TextCompositionStarted += (_, _) => _composing.Add(editor);
            editor.TextCompositionEnded += (_, _) => _composing.Remove(editor);
        }
    }

    private async void OnFieldKey(object sender, KeyRoutedEventArgs args)
    {
        if (
            args.OriginalSource is not TextBox editor
            || _composing.Contains(editor)
            || RowOf(editor)?.Setting is not { } setting
        )
            return;
        if (args.Key == VirtualKey.Escape)
        {
            args.Handled = true;
            setting.Cancel();
            editor.Text = setting.Input;
        }
        else if (args.Key == VirtualKey.Enter && !editor.AcceptsReturn)
        {
            args.Handled = true;
            setting.Input = editor.Text;
            await setting.Commit();
        }
    }

    // Focus on the row's browse button or unit selector stays with the field.
    // Focus outside Settings leaves the draft for Settings to save on leaving.
    private void OnFieldDeparture(object sender, RoutedEventArgs args)
    {
        if (
            args.OriginalSource is not TextBox editor
            || _composing.Contains(editor)
            || RowOf(editor) is not { Setting: { } setting } row
        )
            return;
        for (
            var current = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            current is not null;
            current = VisualTreeHelper.GetParent(current)
        )
        {
            if (current == row)
                return;
            if (current == this)
            {
                setting.Input = editor.Text;
                _ = setting.Depart();
                return;
            }
        }
    }
}

internal sealed record ProviderSetup(ProviderOption Provider, bool EnableAutomaticDownload);
