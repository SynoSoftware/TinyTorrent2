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
using Syno.TinyTorrent.Helpers;
using Syno.TinyTorrent.Models;
using Windows.System;
using Windows.UI.ViewManagement;
using ProviderOption = Syno.TinyTorrent.Library.ProviderOption;
using SubtitleSettings = Syno.TinyTorrent.Subtitles.Settings;

namespace Syno.TinyTorrent.Views;

public sealed partial class SettingsPage : UserControl
{
    private readonly HashSet<TextBox> _composing = [];
    private readonly HashSet<TextBox> _editors = [];
    private readonly HashSet<ComboBox> _choices = [];
    private bool _refreshing;
    private bool _refreshingProviders;
    private bool _showAdvanced;
    private FileInfo? _data;
    private Task<long>? _titles;
    private Task _providerSave = Task.CompletedTask;
    private Task _lookupSave = Task.CompletedTask;
    private bool _lookupEdited;
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
    public Settings Model { get; }
    public MainViewModel Main { get; }
    public event EventHandler<Setting>? FolderRequested;
    public event EventHandler? ProxyRequested;
    internal event EventHandler<SubtitleSupplier>? SupplierRequested;
    internal event EventHandler<ProviderOption>? KeyRequested;
    internal event EventHandler<ProviderOption>? DownloadRequested;
    internal event EventHandler<ProviderOption>? DeleteRequested;
    public event EventHandler? ConnectionRequested;
    internal event EventHandler? LayoutChanged;

    public SettingsPage(MainViewModel main)
    {
        Main = main;
        Model = main.Settings;
        InitializeComponent();
        Categories.ItemInvoked += OnCategoryInvoked;
        // Keep the overhanging corner marks off the navigation pane and caption.
        Root.SizeChanged += Motion.Clip;
        BodyContent.SizeChanged += Motion.Clip;
        PageHost.SizeChanged += Motion.Clip;
        BodyContent.LayoutUpdated += (_, _) => PositionOutline();
        BodyContent.GotFocus += OnFocus;
        _outlineTimer.Tick += (_, _) => ClearOutline();
        _pages.Show(PageContent);
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
        _themeSettings = ThemeSettings.CreateForWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
        _themeSettings.Changed += OnThemeChanged;
        RefreshIcons();
        RefreshText();
        Model.RefreshAdapters();
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
        foreach (var (item, glyph, asset) in new[]
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
        })
        {
            item.Icon = theme.HighContrast
                ? new FontIcon { FontFamily = Lucide.Font, Glyph = glyph, FontSize = size }
                : new ImageIcon
                {
                    Source = new SvgImageSource(new Uri($"ms-appx:///Assets/Settings/{asset}.svg")),
                    Width = size,
                    Height = size,
                };
            AutomationProperties.SetAccessibilityView(item.Icon, AccessibilityView.Raw);
        }
    }

    private void OnText(object? sender, EventArgs args) => RefreshText();

    // Video information lists Off first, then the providers in their order;
    // choosing a provider turns information on with it.
    private int InformationIndex =>
        Main.Library.IsEnrichmentEnabled && Main.Library.Option is { } option
            ? Main.Library.Providers.ToList().IndexOf(option) + 1
            : 0;

    private async void OnVideoInformation(object sender, SelectionChangedEventArgs args)
    {
        var index = VideoInformation.SelectedIndex;
        if (
            _refreshingProviders
            || index < 0
            || !VideoInformation.IsEnabled
            || _providerSave.IsCompleted && index == InformationIndex
        )
            return;
        // A provider without its downloaded data asks to download it, and the
        // finished download turns the provider on; until then the choice stays
        // as it was.
        if (index > 0 && Main.Library.Providers[index - 1] is { Dataset.IsInstalled: false } missing)
        {
            RefreshProviders();
            if (Main.Library.CanDownload)
                DownloadRequested?.Invoke(this, missing);
            return;
        }
        // A provider with no usable key asks for the person's own, and saving
        // it turns the provider on; until then the choice stays as it was.
        if (
            index > 0
            && Main.Library.Providers[index - 1] is { Access: Access.Key } chosen
            && !chosen.HasBuiltInKey
            && !Main.Library.HasKey(chosen.ProviderId)
        )
        {
            RefreshProviders();
            KeyRequested?.Invoke(this, chosen);
            return;
        }
        var save =
            index == 0
                ? Main.Library.Disable()
                : Main.Library.Enable(Main.Library.Providers[index - 1].ProviderId);
        _providerSave = save;
        try
        {
            await save;
        }
        catch (Exception error)
        {
            Main.Report(error);
        }
        finally
        {
            RefreshProviders();
        }
    }

    private void OnProviderKey(object sender, RoutedEventArgs args)
    {
        if (Main.Library.Option is { Access: Access.Key } option)
            KeyRequested?.Invoke(this, option);
    }

    // Updating asks first and states the installed data's age, so the person
    // can judge whether the new download is worth its size.
    private void OnDataDownload(object sender, RoutedEventArgs args)
    {
        if (Main.Library.DataProvider is { Dataset.IsInstalled: true } option)
            DownloadRequested?.Invoke(this, option);
        else
            _ = Main.DownloadData();
    }

    private void OnDataDelete(object sender, RoutedEventArgs args)
    {
        if (Main.Library.DataProvider is { } option)
            DeleteRequested?.Invoke(this, option);
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

    // Library saves the search limit, not the engine, so the limit follows
    // the field rules of Settings.Commit and Settings.Depart here: Enter keeps
    // invalid input with its error, leaving restores the saved limit, and a
    // refused save is reported through the application notification.
    private void OnLookupLoaded(object sender, RoutedEventArgs args)
    {
        if (TextEditor.Find(LookupLimit) is { } editor && _editors.Add(editor))
        {
            Watch(editor);
            editor.TextChanging += (_, _) =>
            {
                _lookupEdited = true;
                LookupsRow.Error = string.Empty;
            };
        }
    }

    private async void OnLookupKey(object sender, KeyRoutedEventArgs args)
    {
        if (TextEditor.Find(LookupLimit) is not { } editor || _composing.Contains(editor))
            return;
        if (args.Key == VirtualKey.Escape)
        {
            args.Handled = true;
            RefreshLookupLimit();
        }
        else if (args.Key == VirtualKey.Enter)
        {
            args.Handled = true;
            if (ParseLookupLimit(editor.Text) is { } limit)
                await SaveLookupLimit(limit);
            else
                LookupsRow.Error = Model.Text.Get("plugins", "invalid_lookups");
        }
    }

    // Leaving the page also saves, because the page's own leave check covers
    // only the engine's settings.
    private async void OnLookupDeparture(object sender, RoutedEventArgs args)
    {
        if (TextEditor.Find(LookupLimit) is not { } editor || _composing.Contains(editor))
            return;
        for (
            var current = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            current is not null;
            current = VisualTreeHelper.GetParent(current)
        )
        {
            if (current == LookupLimit)
                return;
            if (current == this)
                break;
        }
        if (ParseLookupLimit(editor.Text) is not { } limit)
            RefreshLookupLimit();
        else
            await SaveLookupLimit(limit);
    }

    private static int? ParseLookupLimit(string input) =>
        Settings.TryInteger(input, out var limit) && limit >= 1 ? limit : null;

    private Task SaveLookupLimit(int limit)
    {
        if (!_lookupEdited)
            return _lookupSave;
        var input = LookupLimit.Text;
        var previous = _lookupSave;
        return _lookupSave = Save();

        async Task Save()
        {
            await previous;
            if (limit != Main.LookupLimit)
            {
                try
                {
                    await Main.SaveLookupLimit(limit);
                }
                catch (Exception error)
                {
                    Main.Report(error);
                    return;
                }
            }
            if (!_lookupEdited || LookupLimit.Text == input)
                RefreshLookupLimit();
        }
    }

    private void OnProtectionLoaded(object sender, RoutedEventArgs args) => RefreshProtection();

    private void RefreshProtection()
    {
        if (ProtectionScope is null)
            return;
        var selected = (ProtectionScope.SelectedItem as ComboBoxItem)?.Tag;
        ProtectionScope.Items.Clear();
        ProtectionScope.Items.Add(new ComboBoxItem { Content = Model.Text.Get("request_protection", "reset_all") });
        foreach (var provider in Main.Library.Providers)
            ProtectionScope.Items.Add(new ComboBoxItem { Content = provider.Name, Tag = provider.ProviderId });
        if (Main.Subtitles is { } subtitles)
            foreach (var supplier in subtitles.Suppliers)
                ProtectionScope.Items.Add(new ComboBoxItem
                {
                    Content = supplier.SupplierId.ToString(),
                    Tag = supplier.SupplierId.ToString().ToLowerInvariant(),
                });
        ProtectionScope.SelectedItem = ProtectionScope.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => Equals(item.Tag, selected)) ?? ProtectionScope.Items[0];
        ProtectionRow.StateText = string.Empty;
    }

    private async void OnProtectionReset(object sender, RoutedEventArgs args)
    {
        ProtectionReset.IsEnabled = ProtectionScope.IsEnabled = false;
        ProtectionRow.Error = string.Empty;
        try
        {
            await Main.ResetProtection((ProtectionScope.SelectedItem as ComboBoxItem)?.Tag as string);
            Main.ShowOutcome(Model.Text.Get("request_protection", "reset_done"), string.Empty);
        }
        catch (Exception error)
        {
            Main.Report(error);
        }
        finally
        {
            ProtectionReset.IsEnabled = ProtectionScope.IsEnabled = true;
        }
    }

    internal async Task SaveFeatures()
    {
        Task lookup = _lookupSave;
        if (Main.Page == WindowPage.Settings && _category == PluginsCategory)
        {
            if (ParseLookupLimit(LookupLimit.Text) is { } limit)
                lookup = SaveLookupLimit(limit);
            else
                RefreshLookupLimit();
        }
        try
        {
            await _providerSave;
        }
        catch (Exception)
        {
            // The selector already reports and restores a refused choice.
        }
        await lookup;
        if (SubtitlesContent?.Content is SubtitleSettings subtitles)
            await subtitles.Saving;
        if (_automatic is not null)
            await _automatic.Saving;
    }

    private void OnMain(object? sender, PropertyChangedEventArgs args)
    {
        // Preserve the draft when shared settings refresh during an edit.
        if (args.PropertyName is nameof(MainViewModel.LookupLimit) or ""
            && _lookupSave.IsCompleted && !_lookupEdited && LookupLimit is not null
            && TextEditor.Find(LookupLimit)?.FocusState is null or FocusState.Unfocused)
            RefreshLookupLimit();
        if (args.PropertyName is nameof(MainViewModel.CurrentStatus) or nameof(MainViewModel.Page) or "")
            RefreshCoverage();
        if (args.PropertyName is nameof(MainViewModel.Page) or nameof(MainViewModel.Subtitles) or "")
            RefreshSubtitles();
        if (args.PropertyName is nameof(MainViewModel.CurrentStatus) or nameof(MainViewModel.Page)
            or nameof(MainViewModel.Subtitles) or nameof(MainViewModel.FreeSpaceStatus)
            or nameof(MainViewModel.ShowsFreeSpace) or "")
            RefreshHome();
    }

    private void OnProviders(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(Main.Library.Status))
            RefreshHeader();
        if (args.PropertyName is nameof(Main.Library.IsEnrichmentEnabled) or nameof(Main.Library.Providers) or "")
            RefreshHome();
        if (args.PropertyName is nameof(Main.Library.IsAvailable) or "")
            RefreshCoverage();
        if (args.PropertyName is nameof(Main.Library.Providers) or "")
            RefreshProviders();
        else if (args.PropertyName is nameof(Main.Library.DataWork) or nameof(Main.Library.CanDownload))
            RefreshData();
    }

    private void RefreshLookupLimit()
    {
        if (LookupLimit is null)
            return;
        LookupsRow.Error = string.Empty;
        LookupLimit.Text = Main.LookupLimit.ToString(
            System.Globalization.CultureInfo.CurrentCulture
        );
        _lookupEdited = false;
    }

    private void RefreshProviders()
    {
        if (VideoInformation is null)
            return;
        ProviderInformation.IsEnabled = Main.Library.Option is not null;
        // The key button belongs to the provider the box shows, so it hides
        // while information is off.
        ProviderKey.Visibility =
            InformationIndex > 0 && Main.Library.Option is { Access: Access.Key }
                ? Visibility.Visible
                : Visibility.Collapsed;
        var dataset = Main.Library.DataProvider?.Dataset;
        _data = dataset?.Inspect();
        _titles = _data is null ? null : dataset?.CountTitles();
        if (_titles is { IsCompleted: false } counting)
        {
            ShowTitles(counting);
        }
        RefreshData();
        if (!_providerSave.IsCompleted)
            return;
        _refreshingProviders = true;
        VideoInformation.ItemsSource = Main
            .Library.Providers.Select(option => option.Name)
            .Prepend(Model.OffText)
            .ToArray();
        VideoInformation.SelectedIndex = InformationIndex;
        _refreshingProviders = false;
    }

    private void RefreshData()
    {
        if (Plugins is null)
            return;
        // The row has nothing to manage until Library attaches.
        if (Main.Library.DataProvider is not { } option)
        {
            ImdbRow.Visibility = Visibility.Collapsed;
            return;
        }
        ImdbRow.Visibility = Visibility.Visible;
        var section = option.ProviderId;
        var progress = Main.Library.DataWork;
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as Control;
        var focus = focused?.FocusState ?? FocusState.Unfocused;
        var workFocused = DataWork.HasFocus;
        ImdbRow.Description = _data is { } installed
            ? DescribeData(section, installed)
            : Model.Text.Get(section, "data_hint");
        DataDownload.Text = Model.Text.Get(section, _data is null ? "download" : "update");
        ToolTipService.SetToolTip(
            DataDownload,
            Model.Text.Get(section, _data is null ? "download_tip" : "update_tip")
        );
        DataDownload.IsEnabled = Main.Library.CanDownload;
        DataDownload.Visibility = progress is null ? Visibility.Visible : Visibility.Collapsed;
        DataDelete.Visibility =
            progress is null && _data is not null ? Visibility.Visible : Visibility.Collapsed;
        DataWork.Show(progress, Model.Text);
        if (focused is not null
            && (workFocused || ReferenceEquals(focused, DataDownload) || ReferenceEquals(focused, DataDelete))
            && (focused.Visibility == Visibility.Collapsed || !focused.IsEnabled || workFocused && !DataWork.CanFocus))
        {
            if (progress is null && DataDownload.IsEnabled)
                DataDownload.Focus(focus);
            else if (!DataWork.FocusCancel(focus))
                ImdbRow.Focus(FocusState.Programmatic);
        }
    }

    // The age is in whole days, as the update dialog states it.
    private string DescribeData(string section, FileInfo installed)
    {
        var size = Model.Text.Bytes(installed.Length);
        var days = (int)(DateTime.UtcNow - installed.LastWriteTimeUtc).TotalDays;
        var age = days == 0 ? Model.Text.Get(section, "age_new") : Model.Text.FormatCount(section, "age", days);
        return _titles is { IsCompletedSuccessfully: true } titles
            ? Model.Text.Format(section, "data_titles", Model.Text.FormatCount(section, "title_count", titles.Result), size, age)
            : Model.Text.Format(section, "data_summary", size, age);
    }

    // Counting reads the whole data, so the row adds the count when it
    // arrives. Data that cannot be read keeps its size and age.
    private async void ShowTitles(Task<long> titles)
    {
        try
        {
            await titles;
        }
        catch (Library.ProviderException)
        {
            return;
        }
        if (titles == _titles)
        {
            RefreshData();
        }
    }

    private void OnTextScale(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            AlignCategory();
            UpdateHomeLayout();
            ArrangeHeader();
        });

    private void OnModel(object? sender, PropertyChangedEventArgs args)
    {
        _refreshing = true;
        if (Languages is not null)
            Languages.SelectedItem = Model.Text.Language == "es" ? Spanish : English;
        if (EncryptionChoice is not null)
            EncryptionChoice.SelectedItem = SelectedEncryption;
        var unitsChanged = false;
        foreach (var choice in _choices)
        {
            var previous = choice.SelectedItem;
            RefreshChoice(choice);
            unitsChanged |= choice.Tag is Setting { IsDuration: true }
                && !ReferenceEquals(previous, choice.SelectedItem);
        }
        _refreshing = false;
        RefreshHome();
        if (unitsChanged)
            AlignCategory();
    }

    private RadioButton SelectedEncryption =>
        Model.Encryption.Input switch
        {
            "required" => RequiredEncryption,
            "allowed" => AllowedEncryption,
            "disabled" => DisabledEncryption,
            _ => PreferredEncryption,
        };

    internal void RefreshText()
    {
        RefreshProviders();
        RefreshProtection();
        if (!_lookupEdited)
            RefreshLookupLimit();
        _refreshing = true;
        Bindings.Update();
        Label(HomeCategory, "home");
        Label(GeneralCategory, "general");
        Label(TransfersCategory, "transfers");
        Label(LimitsCategory, "limits");
        Label(NetworkCategory, "network");
        Label(AppearanceCategory, "appearance");
        Label(AdvancedCategory, "advanced");
        Label(ScheduleCategory, "schedule");
        Label(PluginsCategory, "plugins");
        RefreshSubtitles();
        Label(AdvancedMode, "show_advanced");
        RefreshAdvancedMode();
        AutomationProperties.SetName(Categories, Model.Text.Get("settings", "categories"));
        if (Transfers is not null)
        {
            DownloadsSection.Header = Model.Text.Get("settings", "files");
            AddingSection.Header = Model.Text.Get("settings", "adding");
            FileSelectionSection.Header = Model.Text.Get("settings", "file_selection");
            WatchedSection.Header = Model.Text.Get("settings", "watched_folder");
            QueueSection.Header = Model.Text.Get("settings", "queue");
            SeedingSection.Header = Model.Text.Get("settings", "seeding");
        }
        if (General is not null)
        {
            ConfirmationsSection.Header = Model.Text.Get("settings", "confirmations");
            NotificationsSection.Header = Model.Text.Get("settings", "notifications");
            NotificationsSection.Description = Model.Text.Get("settings", "notifications_hint");
            LifecycleSection.Header = Model.Text.Get("settings", "startup_closing");
            IndexRow.Header = Model.Text.Get("settings", "settings_index");
            AutomationProperties.SetName(IndexSwitch, IndexRow.Header);
            PowerSection.Header = Model.Text.Get("settings", "power");
        }
        if (Advanced is not null)
        {
            MemorySection.Header = Model.Text.Get("settings", "memory");
            CheckingSection.Header = Model.Text.Get("settings", "checking");
            RefreshSection.Header = Model.Text.Get("settings", "interface");
            HistorySection.Header = Model.Text.Get("settings", "speed_history");
        }
        if (Limits is not null)
        {
            AccountingSection.Header = Model.Text.Get("settings", "bandwidth_accounting");
        }
        if (Network is not null)
        {
            PeersSection.Header = Model.Text.Get("settings", "connections");
            DiscoverySection.Header = Model.Text.Get("settings", "peer_discovery");
            AdapterRow.Description = Model.Text.Get("settings", "adapter_hint");
            ConnectionLimitRow.Description = Model.Text.Get("settings", "connections_hint");
            Label(PreferredEncryption, PreferredLabel, "encryption_recommended");
            Label(RequiredEncryption, RequiredLabel, "encryption_required");
            Label(AllowedEncryption, AllowedLabel, "encryption_allowed");
            Label(DisabledEncryption, DisabledLabel, "encryption_disabled");
            EncryptionChoice.SelectedItem = null;
            EncryptionChoice.SelectedItem = SelectedEncryption;
            ProxyRow.Description = Model.Text.Get("settings", "proxy_hint");
            ProxyRow.Header = Model.Text.Get("settings", "proxy");
        }
        if (Appearance is not null)
        {
            AppearanceSection.Header = Model.Text.Get("settings", "personalization");
            LanguageRow.Header = Model.Text.Get("settings", "language");
            AutomationProperties.SetName(Languages, LanguageRow.Header);
            Label(English, EnglishLabel, "english");
            Label(Spanish, SpanishLabel, "spanish");
            Languages.SelectedItem = null;
            Languages.SelectedItem = Model.Text.Language == "es" ? Spanish : English;
            ThemeRow.Header = Model.Text.Get("settings", "theme");
        }
        foreach (var choice in _choices)
        {
            choice.SelectedItem = null;
            RefreshChoice(choice);
        }
        _refreshing = false;
        RefreshHome();
        (SubtitlesContent?.Content as SubtitleSettings)?.RefreshText();
        _automatic?.Refresh();
        AlignCategory();
    }

    private void Label(Control item, TextBlock label, string key)
    {
        label.Text = Model.Text.Get("settings", key);
        AutomationProperties.SetName(item, label.Text);
    }

    private void Label(NavigationViewItem item, string key)
    {
        if (item.Content is not TextBlock label)
        {
            label = new TextBlock
            {
                Style = (Style)Application.Current.Resources["TinyTorrentNavigationTextStyle"],
            };
            item.Content = label;
        }
        Label(item, label, key);
    }

    private async void OnCategory(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_refreshing || args.SelectedItem is not NavigationViewItem item)
            return;
        var index = Array.IndexOf(CategoryItems, item);
        var target = new SettingTarget(index < 0 ? null : (SettingsCategory)index);
        if (Main.Navigation.Current != WindowPage.Connection && item == (_category ?? HomeCategory))
            return;
        _refreshing = true;
        Categories.SelectedItem = Main.Navigation.Current == WindowPage.Connection ? LimitsCategory : _category ?? HomeCategory;
        _refreshing = false;
        if (await Main.Navigation.SelectCategory(target.Category))
            Navigate(target, focus: true);
    }

    private async void OnCategoryInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
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
        if (Main.Navigation.Current == WindowPage.Connection && args.InvokedItemContainer == LimitsCategory
            && await Main.Navigation.SelectCategory(SettingsCategory.Limits))
            Navigate(new(SettingsCategory.Limits), focus: true);
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
            _pages.Show(content, page is null ? -1 : 1);
        else
            _pages.Replace(content);
        _refreshing = true;
        Categories.SelectedItem = page is null ? _category ?? HomeCategory : LimitsCategory;
        _refreshing = false;
        RefreshSubtitles();
    }

    private void ShowCategory(bool animate = true)
    {
        var next = ReferenceEquals(Categories.SelectedItem, HomeCategory) ? null : Categories.SelectedItem as NavigationViewItem;
        if (_category == next && (_category is null ? IndexContent is not null : CategoryPanels.Any(panel => panel.Name == (string)_category.Tag)))
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
        var content = LoadCategory();
        RefreshText();
        UpdateDisclosure();
        var nextIndex = _category is null ? -1 : Categories.MenuItems.IndexOf(_category);
        // Only the active category contributes to the restored scroll extent.
        foreach (var panel in CategoryPanels.Append(IndexContent).OfType<FrameworkElement>())
            panel.Visibility = ReferenceEquals(panel, content) ? Visibility.Visible : Visibility.Collapsed;
        UpdateLayout();
        RefreshCoverage();
        AlignCategory();
        var position = _category is { } item ? _positions.GetValueOrDefault(item) : _indexPosition;
        Body.ChangeView(null, position, null, true);
        if (animate && !_pages.IsActive)
            _motion.Play(PageContent, nextIndex >= previousIndex ? 24 : -24);
        else
            _motion.Stop();
    }

    private IEnumerable<FrameworkElement> CategoryPanels =>
        new FrameworkElement?[] { General, Transfers, Network, Limits, Appearance, Advanced, Schedule, Plugins }
            .OfType<FrameworkElement>();

    private FrameworkElement LoadCategory()
    {
        var name = _category is null ? nameof(IndexContent) : (string)_category.Tag;
        var existing = _category is null ? IndexContent : CategoryPanels.FirstOrDefault(panel => panel.Name == name);
        if (existing is not null)
            return existing;
        var content = (FrameworkElement)FindName(name);
        if (_category is null)
            InitializeHome();
        else if (_category == GeneralCategory)
            IntegrationContent.Content = _integration = new WindowsIntegration(Model);
        else if (_category == TransfersCategory)
        {
            Watch(Destination);
            Watch(IncompleteFolder);
        }
        else if (_category == AppearanceCategory)
            ThemeContent.Content = _themes = new ThemePicker(Model);
        else if (_category == LimitsCategory)
        {
            _speedLimits = new SpeedLimits(Main, RegisterProfile);
            _speedLimits.ConnectionRequested += OnConnection;
            SpeedContent.Content = _speedLimits;
            AddLink(RelatedLinks, "schedule", () => Main.ScheduleSummary,
                new(SettingsCategory.Schedule, "weekly_schedule"), Syno.Lucide.CalendarClock);
            AddLink(RelatedLinks, "queue", () => Model.QueueSummary,
                new(SettingsCategory.Transfers, "queue"), Syno.Lucide.ListOrdered);
        }
        else if (_category == ScheduleCategory)
        {
            ScheduleContent.Content = _scheduler = new Scheduler(Model.Schedule, new LimitSelector(Main));
            _scheduleLimits = new SpeedLimits(Main, RegisterProfile, inline: true);
            _scheduleLimits.ConnectionRequested += OnConnection;
            ScheduleSpeedContent.Content = _scheduleLimits;
        }
        else if (_category == PluginsCategory)
        {
            DataWork.FocusInvalidated += (_, _) => ImdbRow.Focus(FocusState.Programmatic);
            var subtitles = new SubtitleSettings(Model.Text, Main.RetrySubtitles, ReportSubtitles);
            subtitles.SupplierRequested += (_, supplier) => SupplierRequested?.Invoke(this, supplier);
            SubtitlesContent.Content = subtitles;
        }
        return content;
    }

    private void ReportSubtitles(Exception error, string title) => Main.Report(error, title, string.Empty, null);

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
        AutomationProperties.SetItemStatus(AdvancedMode, _showAdvanced ? Model.OnText : Model.OffText);
    }

    private void UpdateDisclosure()
    {
        AdvancedCategory.Visibility = _showAdvanced
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (DatabaseSummary is not null)
        {
            DatabaseSummary.Visibility = _showAdvanced
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (!_showAdvanced)
                DatabaseSummary.IsExpanded = false;
            else
                RefreshCoverage();
        }
        foreach (var panel in CategoryPanels)
        foreach (var element in Elements(panel))
            if (element is SettingsRow { IsAdvanced: true } or SettingsSection { IsAdvanced: true })
                element.Visibility = _showAdvanced
                    ? Visibility.Visible
                    : Visibility.Collapsed;
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
            foreach (var unit in rows.Select(row => row.Unit).Where(unit => unit.Length > 0).Distinct())
            {
                var label = new TextBlock
                {
                    Text = unit,
                    Style = (Style)Application.Current.Resources["TinyTorrentBodyTextStyle"],
                };
                label.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
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

    private SpeedLimits? CurrentProfiles => _category == ScheduleCategory ? _scheduleLimits : _speedLimits;

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
            Navigate(target, focus: true);
    }

    internal void Navigate(SettingTarget target, bool focus = false, bool animate = true)
    {
        ClearOutline();
        if (target.Category is not { } category)
        {
            _refreshing = true;
            Categories.SelectedItem = HomeCategory;
            _refreshing = false;
            ShowCategory(animate);
            if (!focus)
                return;
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    if (Main.Navigation.Current == WindowPage.Settings && Main.Navigation.Settings == target)
                        Heading.Focus(FocusState.Programmatic);
                }
            );
            return;
        }
        if (category == SettingsCategory.Advanced)
            ShowAdvanced(true);
        _refreshing = true;
        Categories.SelectedItem = CategoryItems[(int)category];
        _refreshing = false;
        ShowCategory(animate);
        if (!focus)
            return;
        Reveal(target);
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (Main.Navigation.Current != WindowPage.Settings || Main.Navigation.Settings != target)
                    return;
                UpdateLayout();
                if (target.Name == "add_period")
                {
                    _scheduler?.FocusAdd();
                    return;
                }
                if (target.Name is null)
                {
                    Heading.Focus(FocusState.Programmatic);
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
            "seeding" => SeedingSection,
            "adding" => AddingSection,
            "download_locations" => DownloadsSection,
            "windows_integration" => _integration?.Section,
            "notifications" => NotificationsSection,
            "power" => PowerSection,
            "dialogs" => ConfirmationsSection,
            "startup_closing" => LifecycleSection,
            "watched_folder" => WatchedSection,
            "deleting_files" => DeletionRow,
            "peer_connections" => PeersSection,
            "peer_discovery" => DiscoverySection,
            "incoming_connections" => IncomingSection,
            "connection_encryption" => EncryptionSection,
            "bandwidth_accounting" => AccountingSection,
            "start_signin" => _integration?.Field("start_signin"),
            "limit_mode" => _category == ScheduleCategory ? _scheduler?.Selector : _speedLimits?.Selector,
            "download_limit" or "upload_limit" or "alternative_download_limit" or "alternative_upload_limit" => CurrentProfiles?.Field(settingName),
            "open_defaults" => _integration?.Field("open_defaults"),
            "settings_index" => IndexSwitch,
            "network_interface" => Adapters,
            "proxy" => ProxyEdit,
            "connection_setup" => CurrentProfiles?.Field("connection_setup"),
            "language" => Languages,
            "theme" => _themes?.Choices,
            "video_information" => VideoInformation,
            "lookup_limit" => LookupLimit,
            "subtitle_automatic"
            or "subtitle_extras"
            or "subtitle_supplier"
            or "subtitle_languages"
            or "subtitle_batch"
            or "subtitle_scan" => (SubtitlesContent?.Content as SubtitleSettings)?.Field(
                settingName
            ),
            _ => CategoryPanels
                .SelectMany(panel => Elements(panel))
                .OfType<Control>()
                .FirstOrDefault(control =>
                    control.Tag is Setting setting && setting.Name == settingName
                ),
        };

    private async void OnToggle(object sender, RoutedEventArgs args)
    {
        if (sender is ToggleSwitch { Tag: Setting setting } control)
            await Model.Toggle(setting, control.IsOn);
    }

    public static bool Recycles(string value) => value == "recycle";

    private string SlowDescription(bool enabled, string key) =>
        Model.Text.Get("settings", enabled ? key : "enable_slow");

    private async void OnRecycling(object sender, RoutedEventArgs args)
    {
        var value = ((ToggleSwitch)sender).IsOn ? "recycle" : "permanent";
        if (!Model.Deletion.CanEdit || value == Model.Deletion.Input)
            return;
        Model.Deletion.Input = value;
        await Model.Commit(Model.Deletion);
    }

    private void OnChoiceLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is not ComboBox choice)
            return;
        _choices.Add(choice);
        _refreshing = true;
        RefreshChoice(choice);
        _refreshing = false;
    }

    private static void RefreshChoice(ComboBox choice)
    {
        if (choice.Tag is Setting setting)
            choice.SelectedItem = choice
                .Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item =>
                    item.Tag is bool value
                        ? value == setting.IsOn
                        : (string)item.Tag
                            == (setting.IsDuration ? setting.DurationUnit : setting.Input)
                );
    }

    private async void OnChoice(object sender, SelectionChangedEventArgs args)
    {
        if (
            _refreshing
            || sender
                is not ComboBox
                {
                    Tag: Setting setting,
                    SelectedItem: ComboBoxItem { Tag: { } value },
                }
        )
            return;
        if (value is bool boolean)
        {
            await Model.Toggle(setting, boolean);
            return;
        }
        if (setting.IsDuration)
        {
            setting.SelectUnit((string)value);
            AlignCategory();
            return;
        }
        if ((string)value == setting.Input)
            return;
        setting.Input = (string)value;
        await Model.Commit(setting);
    }

    private void OnIndexSwitch(object sender, RoutedEventArgs args) =>
        Main.ShowsSettingsIndex = IndexSwitch.IsOn;

    private async void OnAdapter(object sender, SelectionChangedEventArgs args)
    {
        if (
            _refreshing
            || Adapters.SelectedItem is not AdapterChoice choice
            || choice.AdapterId == Model.Adapter.Input
        )
            return;
        Model.Adapter.Input = choice.AdapterId;
        await Model.Commit(Model.Adapter);
    }

    private async void OnEncryption(object sender, SelectionChangedEventArgs args)
    {
        if (
            _refreshing
            || EncryptionChoice.SelectedItem is not RadioButton { Tag: string encryption }
            || encryption == Model.Encryption.Input
        )
            return;
        Model.Encryption.Input = encryption;
        await Model.Commit(Model.Encryption);
    }

    private void OnLanguage(object sender, SelectionChangedEventArgs args)
    {
        if (!_refreshing && Languages.SelectedItem is ComboBoxItem { Tag: string language })
            Model.SelectLanguage(language);
    }

    private void OnFolder(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: Setting setting })
            FolderRequested?.Invoke(this, setting);
    }

    private void OnProxy(object sender, RoutedEventArgs args) =>
        ProxyRequested?.Invoke(this, EventArgs.Empty);

    private void Watch(TextBox editor)
    {
        editor.TextCompositionStarted += (_, _) => _composing.Add(editor);
        editor.TextCompositionEnded += (_, _) => _composing.Remove(editor);
    }

    private void RegisterProfile(TextBox editor)
    {
        if (!_editors.Add(editor))
            return;
        Watch(editor);
        editor.PreviewKeyDown += OnFieldKey;
        editor.LostFocus += OnFieldDeparture;
    }

    private void OnTextLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is TextBox editor && _editors.Add(editor))
            Watch(editor);
    }

    private async void OnFieldKey(object sender, KeyRoutedEventArgs args)
    {
        if (
            sender is not Control { Tag: Setting setting } control
            || TextEditor.Find(control) is not { } editor
            || _composing.Contains(editor)
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
            await Model.Commit(setting);
        }
    }

    private void OnFieldDeparture(object sender, RoutedEventArgs args)
    {
        if (
            sender is not Control { Tag: Setting setting } control
            || TextEditor.Find(control) is not { } editor
            || _composing.Contains(editor)
        )
            return;
        var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        if (
            focused is Control { Tag: Setting target }
            && ReferenceEquals(target, setting)
            && (focused is Button || setting.IsDuration && focused is ComboBox)
        )
            return;
        for (
            var current = focused;
            current is not null;
            current = VisualTreeHelper.GetParent(current)
        )
        {
            if (current == control)
                return;
            if (current == this)
            {
                setting.Input = editor.Text;
                _ = Model.Depart(setting);
                return;
            }
        }
    }
}
