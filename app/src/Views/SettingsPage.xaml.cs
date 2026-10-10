using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Helpers;
using Syno.TinyTorrent.Models;
using SubtitleSettings = Syno.TinyTorrent.Subtitles.Settings;
using Windows.System;
using Windows.UI.ViewManagement;

namespace Syno.TinyTorrent.Views;

public sealed partial class SettingsPage : UserControl
{
    private readonly HashSet<TextBox> _composing = [];
    private readonly HashSet<TextBox> _editors = [];
    private readonly HashSet<ComboBox> _choices = [];
    private bool _refreshing;
    private bool _refreshingProviders;
    private Exception? _providerFailure;
    private Task _providerSave = Task.CompletedTask;
    private readonly Scheduler _scheduler;
    private readonly UISettings _display = new();
    private readonly Motion _motion = new();
    private readonly Dictionary<SelectorBarItem, double> _positions = [];
    private SelectorBarItem? _category;
    private bool _restoringCategory;
    private double _indexPosition;
    public Settings Model { get; }
    public MainViewModel Main { get; }
    public event EventHandler<Setting>? FolderRequested;
    public event EventHandler? ProxyRequested;
    public event EventHandler? SupplierRequested;
    public event EventHandler? ConnectionRequested;
    internal event EventHandler? LayoutChanged;

    public SettingsPage(MainViewModel main)
    {
        Main = main;
        Model = main.Settings;
        InitializeComponent();
        // Keep the overhanging corner marks off the navigation pane and caption.
        Root.SizeChanged += Motion.Clip;
        BodyContent.SizeChanged += Motion.Clip;
        _motion.Show(IndexContent);
        _scheduler = new Scheduler(Model.Schedule);
        ScheduleContent.Content = _scheduler;
        var subtitles = new SubtitleSettings(Model.Text, Main.RetrySubtitles);
        subtitles.SupplierRequested += (_, _) => SupplierRequested?.Invoke(this, EventArgs.Empty);
        subtitles.ProblemChanged += (_, _) => RefreshSubtitles();
        SubtitlesContent.Content = subtitles;
        Watch(Destination);
        Watch(IncompleteFolder);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        RefreshText();
        UpdateDisclosure();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        Model.TextChanged += OnText;
        Model.PropertyChanged += OnModel;
        Main.PropertyChanged += OnSubtitleMain;
        Main.Library.PropertyChanged += OnProviders;
        RefreshSubtitles();
        _display.TextScaleFactorChanged += OnTextScale;
        RefreshText();
        Model.RefreshAdapters();
        _ = Model.ObserveRegistration();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _motion.Stop();
        Model.TextChanged -= OnText;
        Model.PropertyChanged -= OnModel;
        Main.PropertyChanged -= OnSubtitleMain;
        Main.Library.PropertyChanged -= OnProviders;
        _display.TextScaleFactorChanged -= OnTextScale;
    }

    private void OnText(object? sender, EventArgs args) => RefreshText();

    private async void OnProvider(object sender, SelectionChangedEventArgs args)
    {
        if (_refreshingProviders || sender is not ComboBox { SelectedIndex: >= 0, IsEnabled: true } choice ||
            _providerSave.IsCompleted && choice.SelectedIndex == Main.Library.ProviderIndex ||
            choice.SelectedIndex >= Main.Library.Providers.Count)
            return;
        var option = Main.Library.Providers[choice.SelectedIndex];
        _providerFailure = null;
        ProviderRow.Error = string.Empty;
        var save = Main.Library.SelectProvider(option.ProviderId);
        _providerSave = save;
        try { await save; }
        catch (Exception error) { if (ReferenceEquals(_providerSave, save)) _providerFailure = error; }
        finally { RefreshProviders(); }
    }

    private void OnProviders(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(Main.Library.Providers) or "")
            RefreshProviders();
    }

    private void RefreshProviders()
    {
        ProviderRow.Error = _providerFailure is { } failure ? Main.Library.Error(failure) : string.Empty;
        if (!_providerSave.IsCompleted)
            return;
        _refreshingProviders = true;
        ProviderChoice.ItemsSource = Main.Library.Providers
            .Select(option => Model.Text.Get(option.TextSection, "name")).ToArray();
        ProviderChoice.SelectedIndex = Main.Library.ProviderIndex;
        _refreshingProviders = false;
    }

    private void OnTextScale(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(AlignCategory);

    private async void OnLimitsChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is ComboBox { SelectedIndex: >= 0 } choice && choice.SelectedIndex != Main.LimitsIndex)
            await Main.ChooseLimits((LimitMode)choice.SelectedIndex);
    }

    private void OnModel(object? sender, PropertyChangedEventArgs args)
    {
        _refreshing = true;
        Languages.SelectedItem = Model.Text.Language == "es" ? Spanish : English;
        Theme.SelectedItem = Model.Theme.Input switch
        {
            "light" => LightTheme,
            "dark" => DarkTheme,
            _ => SystemTheme,
        };
        EncryptionChoice.SelectedItem = SelectedEncryption;
        foreach (var choice in _choices)
            RefreshChoice(choice);
        _refreshing = false;
        AlignCategory();
    }

    private ComboBoxItem SelectedEncryption =>
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
        _refreshing = true;
        Bindings.Update();
        PageTitle.Text = Model.Text.Get("finding", "settings");
        Label(GeneralCategory, "general");
        Label(TransfersCategory, "transfers");
        Label(LimitsCategory, "limits");
        Label(NetworkCategory, "network");
        Label(AppearanceCategory, "appearance");
        Label(AdvancedCategory, "advanced");
        Label(ScheduleCategory, "schedule");
        Label(SubtitlesCategory, "subtitles");
        RefreshSubtitles();
        AdvancedLabel.Text = Model.Text.Get("settings", "show_advanced");
        AutomationProperties.SetName(AdvancedSwitch, AdvancedLabel.Text);
        AutomationProperties.SetName(Categories, Model.Text.Get("settings", "categories"));
        Label(DownloadsSection, "files", "downloads_hint");
        Label(AddingSection, "adding", "show_add_hint");
        Label(FileSelectionSection, "file_selection", "file_selection_hint");
        Label(WatchedSection, "watched_folder", "watch_hint");
        IntegrationSection.Header = Model.Text.Get("settings", "windows_integration");
        Label(MemorySection, "memory", "memory_hint");
        Label(CheckingSection, "checking", "checking_hint");
        ShowAddRow.Description = Model.Text.Get("settings", "show_add_hint");
        NotificationsSection.Header = Model.Text.Get("settings", "notifications");
        LifecycleSection.Header = Model.Text.Get("settings", "startup_closing");
        SignInRow.Header = Model.Text.Get("settings", "start_signin");
        AutomationProperties.SetName(Startup, SignInRow.Header);
        TrayRow.Description = Model.Text.Get("settings", "start_in_tray_hint");
        HandlersRow.Header = Model.Text.Get("settings", "open_defaults");
        AutomationProperties.SetName(Handlers, HandlersRow.Header);
        Label(PowerSection, "power", "power_hint");
        Label(UpdatesSection, "updates", "updates_hint");
        Label(CapsSection, "standard_caps", "speed_hint");
        Label(AlternativeSection, "alternative_caps", "speed_hint");
        Label(PeersSection, "connections", "connections_hint");
        Label(ModeSection, "limits", "schedule_hint");
        Label(SummarySection, "caps", "speed_hint");
        Label(AccountingSection, "bandwidth_accounting", "bandwidth_accounting_hint");
        Label(DiscoverySection, "peer_discovery", "peer_discovery_hint");
        Label(RefreshSection, "interface", "refresh_interval_hint");
        Label(HistorySection, "speed_history", "speed_history_hint");
        Label(WindowSection, "window", "title_speeds_hint");
        Label(StatusSection, "status_bar", "free_space_hint");
        Label(QueueSection, "queue", "queue_hint");
        Label(SeedingSection, "seeding", "seeding_hint");
        Label(NetworkSection, "connections", "network_hint");
        AdapterRow.Description = Model.Text.Get("settings", "adapter_hint");
        ConnectionLimitRow.Description = Model.Text.Get("settings", "connections_hint");
        EncryptionRow.Description = Model.Text.Get("settings", "encryption_hint");
        Label(PreferredEncryption, PreferredLabel, "encryption_preferred");
        Label(RequiredEncryption, RequiredLabel, "encryption_required");
        Label(AllowedEncryption, AllowedLabel, "encryption_allowed");
        Label(DisabledEncryption, DisabledLabel, "encryption_disabled");
        EncryptionChoice.SelectedItem = null;
        EncryptionChoice.SelectedItem = SelectedEncryption;
        Label(ProxySection, "proxy_server", "proxy_hint");
        ProxyRow.Header = Model.Text.Get("settings", "proxy");
        Label(ScheduleSection, "limits_apply", "schedule_hint");
        Label(AppearanceSection, "appearance", "appearance_hint");
        LanguageRow.Header = Model.Text.Get("settings", "language");
        AutomationProperties.SetName(Languages, LanguageRow.Header);
        Label(English, EnglishLabel, "english");
        Label(Spanish, SpanishLabel, "spanish");
        Languages.SelectedItem = null;
        Languages.SelectedItem = Model.Text.Language == "es" ? Spanish : English;
        ThemeRow.Header = Model.Text.Get("settings", "theme");
        AutomationProperties.SetName(Theme, ThemeRow.Header);
        Label(SystemTheme, SystemLabel, "system_theme");
        Label(LightTheme, LightLabel, "light_theme");
        Label(DarkTheme, DarkLabel, "dark_theme");
        Theme.SelectedItem = null;
        Theme.SelectedItem = Model.Theme.Input switch
        {
            "light" => LightTheme,
            "dark" => DarkTheme,
            _ => SystemTheme,
        };
        foreach (var choice in _choices)
        {
            choice.SelectedItem = null;
            RefreshChoice(choice);
        }
        foreach (var choice in new[] { LimitsChoice, ScheduleLimitsChoice })
        {
            choice.SelectedIndex = -1;
            choice.SelectedIndex = Main.LimitsIndex;
        }
        _refreshing = false;
        RefreshIndex();
        (SubtitlesContent.Content as SubtitleSettings)?.Refresh();
        AlignCategory();
    }

    private void Label(ContentControl control, string key, string group = "settings")
    {
        control.Content = Model.Text.Get(group, key);
        AutomationProperties.SetName(control, (string)control.Content);
    }

    private void Label(ComboBoxItem item, TextBlock label, string key)
    {
        label.Text = Model.Text.Get("settings", key);
        AutomationProperties.SetName(item, label.Text);
    }

    private void Label(SelectorBarItem item, string key)
    {
        item.Text = Model.Text.Get("settings", key);
        AutomationProperties.SetName(item, item.Text);
    }

    private void Label(SettingsSection section, string header, string description)
    {
        section.Header = Model.Text.Get("settings", header);
        section.Description = Model.Text.Get("settings", description);
    }

    private async void OnCategory(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_restoringCategory)
            return;
        var requested = sender.SelectedItem;
        var canLeave = await Model.PrepareLeave();
        if (sender.SelectedItem != requested)
            return;
        if (canLeave)
        {
            ShowCategory();
            return;
        }
        _restoringCategory = true;
        try
        {
            sender.SelectedItem = _category;
            var field = Recover(null);
            ShowCategory();
            field?.Focus(FocusState.Programmatic);
        }
        finally
        {
            _restoringCategory = false;
        }
    }

    private void ShowCategory()
    {
        if (_category is null && Categories.SelectedItem is not null)
            Depart();
        var previousIndex = _category is null ? -1 : Categories.Items.IndexOf(_category);
        if (_category is { } previous)
            _positions[previous] = Body.VerticalOffset;
        else
            _indexPosition = Body.VerticalOffset;
        _category = Categories.SelectedItem;
        Categories.Visibility = _category is null ? Visibility.Collapsed : Visibility.Visible;
        var nextIndex = _category is null ? -1 : Categories.Items.IndexOf(_category);
        var content = _category is null ? IndexContent :
            CategoryPanels.First(panel => panel.Name == (string)_category.Tag);
        _motion.Show(content, nextIndex >= previousIndex ? 1 : -1);
        UpdateLayout();
        AlignCategory();
        var position = _category is { } item ? _positions.GetValueOrDefault(item) : _indexPosition;
        Body.ChangeView(null, position, null, true);
    }

    private IEnumerable<FrameworkElement> CategoryPanels =>
        CategoryContent.Children.OfType<FrameworkElement>();

    private void RefreshIndex()
    {
        CategoryIndex.Children.Clear();
        CategoryIndex.RowDefinitions.Clear();
        var items = Categories.Items.Where(item => item != AdvancedCategory || AdvancedSwitch.IsOn).ToArray();
        for (var index = 0; index < items.Length; index++)
        {
            if (index % 2 == 0)
                CategoryIndex.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var item = items[index];
            var panel = CategoryPanels.First(panel => panel.Name == (string)item.Tag);
            var content = new StackPanel();
            if (item.Icon is FontIcon icon)
                content.Children.Add(new FontIcon
                {
                    FontFamily = icon.FontFamily,
                    Glyph = icon.Glyph,
                    Style = (Style)Application.Current.Resources["TinyTorrentSurfaceIconStyle"],
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 0, 12),
                });
            content.Children.Add(new TextBlock
            {
                Text = item.Text,
                Style = (Style)Application.Current.Resources["TinyTorrentGroupTitleTextStyle"],
            });
            var description = string.Join(" · ", Elements(panel)
                .OfType<SettingsSection>()
                .Where(section => !section.IsAdvanced || AdvancedSwitch.IsOn)
                .Select(section => section.Header)
                .Take(4));
            if (item == SubtitlesCategory)
                description = Model.Text.Get("subtitles", "summary");
            content.Children.Add(new TextBlock
            {
                Text = description,
                Style = (Style)Application.Current.Resources["TinyTorrentTitleDetailTextStyle"],
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 8, 0, 0),
            });
            if (item == SubtitlesCategory)
            {
                _subtitleProblem = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                    Margin = new Thickness(0, 8, 0, 0),
                };
                content.Children.Add(_subtitleProblem);
                RefreshSubtitles();
            }
            var button = new Button
            {
                Content = content,
                Tag = item,
                Style = (Style)Resources["SettingsIndexCardStyle"],
            };
            AutomationProperties.SetName(button, item.Text);
            AutomationProperties.SetHelpText(button, description);
            button.Click += (_, _) =>
            {
                Categories.SelectedItem = item;
                item.Focus(FocusState.Programmatic);
            };
            Grid.SetColumn(button, index % 2);
            Grid.SetRow(button, index / 2);
            CategoryIndex.Children.Add(button);
        }
    }

    private async void OnAdvanced(object sender, RoutedEventArgs args)
    {
        if (!AdvancedSwitch.IsOn && !await Model.PrepareLeave())
        {
            AdvancedSwitch.IsOn = true;
            Recover(null)?.Focus(FocusState.Programmatic);
            return;
        }
        UpdateDisclosure();
    }

    private void UpdateDisclosure()
    {
        AdvancedCategory.Visibility = AdvancedSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;
        foreach (var panel in CategoryPanels)
            foreach (var element in Elements(panel))
                if (element is SettingsRow { IsAdvanced: true } or SettingsSection { IsAdvanced: true })
                    element.Visibility = AdvancedSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;
        if (!AdvancedSwitch.IsOn && Categories.SelectedItem == AdvancedCategory)
            Categories.SelectedItem = null;
        RefreshIndex();
        AlignCategory();
    }

    private void AlignCategory()
    {
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        if (_category is null)
            return;
        var panel = CategoryPanels.First(panel => panel.Name == (string)_category.Tag);
        if (_category == SubtitlesCategory && SubtitlesContent.Content is SubtitleSettings subtitles)
            ActionButton.Align(subtitles.Actions);
        var rows = Elements(panel, visible: true).OfType<SettingsRow>().ToArray();
        var units = rows.Select(row => row.Unit).Where(unit => unit.Length > 0).ToArray();
        var hasUnits = units.Length > 0 || rows.Any(row => row.UnitSelector is not null);
        var labels = hasUnits && rows.Any(row => row.Content is ToggleSwitch)
            ? units.Concat([Model.OnText, Model.OffText])
            : units;
        var width = 0d;
        foreach (var text in labels)
        {
            var label = new TextBlock { Text = text };
            label.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            width = Math.Max(width, label.DesiredSize.Width);
        }
        foreach (var selector in rows.Select(row => row.UnitSelector).OfType<ComboBox>())
        {
            selector.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            width = Math.Max(width, selector.DesiredSize.Width);
        }
        foreach (var row in rows)
            row.Align(width);
    }

    internal double MeasureWidth()
    {
        Categories.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        PageContent.MaxWidth = Math.Max(1000, Categories.DesiredSize.Width);
        return PageContent.MaxWidth + PageContent.Margin.Left + PageContent.Margin.Right;
    }

    private static IEnumerable<FrameworkElement> Elements(FrameworkElement element, bool visible = false)
    {
        if (visible && element.Visibility != Visibility.Visible)
            yield break;
        yield return element;
        var children = element switch
        {
            Panel panel => panel.Children.OfType<FrameworkElement>(),
            SettingsRow row => new[] { row.Content, row.Detail, row.UnitSelector }.OfType<FrameworkElement>(),
            ContentControl control => new[] { control.Content }.OfType<FrameworkElement>(),
            _ => [],
        };
        foreach (var child in children)
            foreach (var descendant in Elements(child, visible))
                yield return descendant;
    }

    private void OnEditLimits(object sender, RoutedEventArgs args) => Navigate(new(SettingsCategory.Limits, "download_limit"));

    private void OnConnection(object sender, RoutedEventArgs args) => ConnectionRequested?.Invoke(this, EventArgs.Empty);

    internal void FocusConnection() => ConnectionEdit.Focus(FocusState.Programmatic);

    // In SettingsCategory order.
    private SelectorBarItem[] CategoryItems =>
        [
            GeneralCategory,
            TransfersCategory,
            NetworkCategory,
            LimitsCategory,
            AppearanceCategory,
            AdvancedCategory,
            ScheduleCategory,
            SubtitlesCategory,
        ];

    internal SettingsCategory? Category
    {
        get
        {
            var index = Array.IndexOf(CategoryItems, Categories.SelectedItem);
            return index >= 0 ? (SettingsCategory)index : null;
        }
    }

    internal Control? Recover(string? focusName)
    {
        if (Model.RefusedSetting is { } setting)
        {
            Reveal(new(setting.Category, setting.Name));
            Categories.SelectedItem = CategoryItems[(int)setting.Category];
            var control = FindControl(setting.Name) ?? Categories;
            return control is NumberBox ? TextEditor.Find(control) ?? control : control;
        }
        if (!Model.Schedule.HasDraft || !Model.Schedule.HasError)
            return null;
        Categories.SelectedItem = ScheduleCategory;
        return _scheduler.Field(focusName);
    }

    internal void Navigate(SettingTarget target)
    {
        if (target.Category is not { } category)
        {
            Categories.SelectedItem = null;
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => CategoryIndex.Children.OfType<Button>().FirstOrDefault()?.Focus(FocusState.Programmatic)
            );
            return;
        }
        Reveal(target);
        Categories.SelectedItem = CategoryItems[(int)category];
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                UpdateLayout();
                if (target.Name == "add_period")
                {
                    _scheduler.FocusAdd();
                    return;
                }
                var control = FindControl(target.Name);
                if (control is null)
                    return;
                if (control == Categories)
                {
                    Categories.SelectedItem?.Focus(FocusState.Programmatic);
                    return;
                }
                control.StartBringIntoView();
                if (control is NumberBox && TextEditor.Find(control) is { } editor)
                    editor.Focus(FocusState.Programmatic);
                else
                    control.Focus(FocusState.Programmatic);
            }
        );
    }

    private void Reveal(SettingTarget target)
    {
        if (target.Category == SettingsCategory.Advanced)
        {
            AdvancedSwitch.IsOn = true;
            return;
        }
        var advanced = CategoryPanels
            .SelectMany(panel => Elements(panel))
            .Where(element => element is SettingsRow { IsAdvanced: true } or SettingsSection { IsAdvanced: true });
        foreach (var element in advanced)
        {
            var controls = Elements(element).OfType<Control>();
            if (!controls.Any(control => control.Tag is Setting setting && setting.Name == target.Name))
                continue;
            AdvancedSwitch.IsOn = true;
            return;
        }
    }

    private Control? FindControl(string? settingName) =>
        settingName switch
        {
            "start_signin" => Startup,
            "limit_mode" => LimitsChoice,
            "startup_settings" => StartupSettings,
            "open_defaults" => Handlers,
            "network_interface" => Adapters,
            "proxy" => ProxyEdit,
            "connection_setup" => ConnectionEdit,
            "language" => Languages,
            "theme" => Theme,
            "library_provider" => ProviderChoice,
            "video_information" => VideoInformation,
            "subtitle_automatic" or "subtitle_supplier" or "subtitle_languages" or "subtitle_finished" or "subtitle_files" =>
                (SubtitlesContent.Content as SubtitleSettings)?.Field(settingName),
            null => Categories,
            _ => CategoryPanels.SelectMany(panel => Elements(panel)).OfType<Control>()
                .FirstOrDefault(control => control.Tag is Setting setting && setting.Name == settingName),
        };

    private async void OnToggle(object sender, RoutedEventArgs args)
    {
        if (sender is ToggleSwitch { Tag: Setting setting } control)
            await Model.Toggle(setting, control.IsOn);
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
            choice.SelectedItem = choice.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
                item.Tag is bool value ? value == setting.IsOn :
                (string)item.Tag == (setting.IsDuration ? setting.DurationUnit : setting.Input));
    }

    private async void OnChoice(object sender, SelectionChangedEventArgs args)
    {
        if (_refreshing
            || sender is not ComboBox
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
            return;
        }
        if ((string)value == setting.Input)
            return;
        setting.Input = (string)value;
        await Model.Commit(setting);
    }

    private async void OnStartup(object sender, RoutedEventArgs args)
    {
        if (!_refreshing && Model.HasRegistration && Startup.IsOn != Model.Startup)
            await Model.SetStartup(Startup.IsOn);
    }

    private async void OnHandlers(object sender, RoutedEventArgs args)
    {
        if (!_refreshing && Model.HasRegistration && Handlers.IsOn != Model.HandlersRegistered)
            await Model.SetHandlers(Handlers.IsOn);
    }

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
            || EncryptionChoice.SelectedItem is not ComboBoxItem { Tag: string encryption }
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

    private async void OnTheme(object sender, SelectionChangedEventArgs args)
    {
        if (
            !_refreshing
            && Model.CanSelectTheme
            && Theme.SelectedItem is ComboBoxItem { Tag: string theme }
        )
            await Model.SelectTheme(theme);
    }

    private void OnFolder(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: Setting setting })
            FolderRequested?.Invoke(this, setting);
    }

    private void OnProxy(object sender, RoutedEventArgs args) =>
        ProxyRequested?.Invoke(this, EventArgs.Empty);

    private void OnNumberLoaded(object sender, RoutedEventArgs args)
    {
        if (
            sender is NumberBox { Tag: Setting setting } number
            && TextEditor.Find(number) is { } editor
            && _editors.Add(editor)
        )
        {
            // NumberBox's focused TextBox does not inherit its HelpText.
            void ForwardHelp() =>
                AutomationProperties.SetHelpText(editor, AutomationProperties.GetHelpText(number));
            number.RegisterPropertyChangedCallback(
                AutomationProperties.HelpTextProperty,
                (_, _) => ForwardHelp()
            );
            ForwardHelp();
            Watch(editor);
            editor.TextChanged += (_, _) =>
            {
                if (number.IsEnabled)
                    setting.Input = editor.Text;
            };
        }
    }

    private void Watch(TextBox editor)
    {
        editor.TextCompositionStarted += (_, _) => _composing.Add(editor);
        editor.TextCompositionEnded += (_, _) => _composing.Remove(editor);
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

    private async void OnFieldDeparture(object sender, RoutedEventArgs args)
    {
        if (
            sender is not Control { Tag: Setting setting } control
            || TextEditor.Find(control) is not { } editor
            || _composing.Contains(editor)
        )
            return;
        var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        if (focused is Control { Tag: Setting target }
            && ReferenceEquals(target, setting)
            && (focused is Button || setting.IsDuration && focused is ComboBox))
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
                if (!await Model.Depart(setting))
                    Recover(null)?.Focus(FocusState.Programmatic);
                return;
            }
        }
    }
}
