using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Helpers;
using Syno.TinyTorrent.Models;

namespace Syno.TinyTorrent.Views;

public sealed partial class PreferencesForm : UserControl
{
    private readonly HashSet<TextBox> _composing = [];
    private readonly HashSet<TextBox> _editors = [];
    private bool _refreshing;
    private readonly Scheduler _scheduler;
    public Preferences Model { get; }
    public MainViewModel Main { get; }
    public event EventHandler? DestinationRequested;
    public event EventHandler? ProxyRequested;

    public PreferencesForm(MainViewModel main)
    {
        Main = main;
        Model = main.Preferences;
        InitializeComponent();
        _scheduler = new Scheduler(Model.Schedule, main);
        ScheduleContent.Content = _scheduler;
        Watch(Destination);
        Categories.SelectedItem = GeneralCategory;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        RefreshText();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        Model.TextChanged += OnText;
        Model.PropertyChanged += OnModel;
        RefreshText();
        Model.RefreshInterfaces();
        _ = Model.ObserveRegistration();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        Model.TextChanged -= OnText;
        Model.PropertyChanged -= OnModel;
    }

    private void OnText(object? sender, EventArgs args) => RefreshText();
    private async void OnLimitsChanged(object sender, SelectionChangedEventArgs args)
    {
        if (LimitsChoice.SelectedIndex >= 0 && LimitsChoice.SelectedIndex != Main.LimitsIndex)
            await Main.ChooseLimits((LimitMode)LimitsChoice.SelectedIndex);
    }

    private void OnModel(object? sender, PropertyChangedEventArgs args)
    {
        _refreshing = true;
        Languages.SelectedItem = Model.Text.Language == "es" ? Spanish : English;
        Theme.SelectedItem = Model.Theme.Input switch { "light" => LightTheme, "dark" => DarkTheme, _ => SystemTheme };
        EncryptionChoice.SelectedItem = SelectedEncryption;
        _refreshing = false;
    }

    private ComboBoxItem SelectedEncryption => Model.Encryption.Input switch
    {
        "required" => RequiredEncryption,
        "allowed" => AllowedEncryption,
        "disabled" => DisabledEncryption,
        _ => PreferredEncryption
    };

    internal void RefreshText()
    {
        _refreshing = true;
        PageTitle.Text = Model.Text.Get("finding", "settings");
        Label(GeneralCategory, "general");
        Label(TransfersCategory, "transfers");
        Label(LimitsCategory, "limits");
        Label(NetworkCategory, "network");
        Label(AppearanceCategory, "appearance");
        AutomationProperties.SetName(Categories, Model.Text.Get("preferences", "categories"));
        Label(DownloadsSection, "adding", "downloads_hint");
        ShowAddRow.Description = Model.Text.Get("preferences", "show_add_hint");
        NotificationsSection.Header = Model.Text.Get("preferences", "notifications");
        Label(StartupSection, "startup", "startup_hint");
        SignInRow.Header = Model.Text.Get("preferences", "start_signin");
        AutomationProperties.SetName(Startup, SignInRow.Header);
        TrayRow.Description = Model.Text.Get("preferences", "start_in_tray_hint");
        HandlersRow.Header = Model.Text.Get("preferences", "open_defaults");
        AutomationProperties.SetName(Handlers, HandlersRow.Header);
        Label(PowerSection, "power", "power_hint");
        Label(UpdatesSection, "updates", "updates_hint");
        Label(CapsSection, "caps", "speed_hint");
        Label(QueueSection, "queue", "queue_hint");
        Label(SeedingSection, "seeding", "seeding_hint");
        Label(NetworkSection, "connections", "network_hint");
        InterfaceRow.Description = Model.Text.Get("preferences", "interface_hint");
        ConnectionsRow.Description = Model.Text.Get("preferences", "connections_hint");
        EncryptionRow.Description = Model.Text.Get("preferences", "encryption_hint");
        Label(PreferredEncryption, PreferredLabel, "encryption_preferred");
        Label(RequiredEncryption, RequiredLabel, "encryption_required");
        Label(AllowedEncryption, AllowedLabel, "encryption_allowed");
        Label(DisabledEncryption, DisabledLabel, "encryption_disabled");
        EncryptionChoice.SelectedItem = SelectedEncryption;
        Label(ProxySection, "proxy_server", "proxy_hint");
        ProxyRow.Header = Model.Text.Get("preferences", "proxy");
        Label(StartupSettings, "startup_settings");
        Label(OpenDefaults, "defaults_settings");
        Label(ScheduleSection, "limits_apply", "schedule_hint");
        Label(AppearanceSection, "appearance", "appearance_hint");
        LanguageRow.Header = Model.Text.Get("preferences", "language");
        AutomationProperties.SetName(Languages, LanguageRow.Header);
        Label(English, EnglishLabel, "english");
        Label(Spanish, SpanishLabel, "spanish");
        Languages.SelectedItem = Model.Text.Language == "es" ? Spanish : English;
        ThemeRow.Header = Model.Text.Get("preferences", "theme");
        AutomationProperties.SetName(Theme, ThemeRow.Header);
        Label(SystemTheme, SystemLabel, "system_theme");
        Label(LightTheme, LightLabel, "light_theme");
        Label(DarkTheme, DarkLabel, "dark_theme");
        Theme.SelectedItem = Model.Theme.Input switch { "light" => LightTheme, "dark" => DarkTheme, _ => SystemTheme };
        _refreshing = false;
    }

    private void Label(ContentControl control, string key, string group = "preferences")
    {
        control.Content = Model.Text.Get(group, key);
        AutomationProperties.SetName(control, (string)control.Content);
    }

    private void Label(ComboBoxItem item, TextBlock label, string key)
    {
        label.Text = Model.Text.Get("preferences", key);
        AutomationProperties.SetName(item, label.Text);
    }

    private void Label(SelectorBarItem item, string key)
    {
        item.Text = Model.Text.Get("preferences", key);
        AutomationProperties.SetName(item, item.Text);
    }

    private void Label(SettingsSection section, string header, string description)
    {
        section.Header = Model.Text.Get("preferences", header);
        section.Description = Model.Text.Get("preferences", description);
    }

    // The corner marks overhang the page; the clip keeps them off the navigation
    // pane and the caption.
    private void OnRootSize(object sender, SizeChangedEventArgs args) =>
        Root.Clip = new RectangleGeometry { Rect = new(0, 0, args.NewSize.Width, args.NewSize.Height) };

    // A row holding two fields shows the first field's message.
    public static string Either(string first, string second) => first.Length > 0 ? first : second;

    private void OnCategory(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is not SelectorBarItem item) return;
        foreach (var panel in new FrameworkElement[] { General, Transfers, Network, Limits, Appearance })
            panel.Visibility = panel.Name == (string)item.Tag ? Visibility.Visible : Visibility.Collapsed;
        Body.ChangeView(null, 0, null, true);
    }

    // In PreferenceSection order.
    private SelectorBarItem[] Sections => [GeneralCategory, TransfersCategory, NetworkCategory, LimitsCategory, AppearanceCategory];

    internal PreferenceSection Section =>
        Array.IndexOf(Sections, Categories.SelectedItem) is var index and >= 0 ? (PreferenceSection)index : PreferenceSection.General;

    internal Control? Recover(string? focusName)
    {
        if (Model.RefusedField is { } field)
        {
            Categories.SelectedItem = Sections[(int)field.Section];
            var control = FindControl(field.Name) ?? Categories;
            return control is NumberBox ? TextEditor.Find(control) ?? control : control;
        }
        if (!Model.Schedule.HasDraft || !Model.Schedule.HasScheduleError) return null;
        Categories.SelectedItem = LimitsCategory;
        return _scheduler.Editor(focusName);
    }

    internal void Navigate(PreferenceTarget target)
    {
        Categories.SelectedItem = Sections[(int)target.Section];
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            UpdateLayout();
            if (target.Field == "add_period" && Main.FollowsSchedule) { _scheduler.FocusAdd(); return; }
            var control = FindControl(target.Field);
            if (control is null) return;
            control.StartBringIntoView();
            if (control is NumberBox && TextEditor.Find(control) is { } editor) editor.Focus(FocusState.Programmatic);
            else control.Focus(FocusState.Programmatic);
        });
    }

    private Control? FindControl(string? field) => field switch
    {
        "start_signin" => Startup,
        // Periods can be added only under the weekly schedule, which the
        // choice offers.
        "limit_mode" or "add_period" => LimitsChoice,
        "startup_settings" => StartupSettings,
        "open_defaults" => Handlers,
        "network_interface" => Interfaces,
        "proxy" => ProxyEdit,
        "language" => Languages,
        "theme" => Theme,
        null => Categories,
        _ => FindField(this, field)
    };

    private static Control? FindField(DependencyObject element, string key)
    {
        if (element is Control { Tag: Preference field } control && field.Name == key) return control;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            if (FindField(VisualTreeHelper.GetChild(element, index), key) is { } child) return child;
        return null;
    }

    private async void OnToggle(object sender, RoutedEventArgs args)
    {
        if (sender is ToggleSwitch { Tag: Preference field } control) await Model.Toggle(field, control.IsOn);
    }
    private async void OnStartup(object sender, RoutedEventArgs args)
    {
        if (!_refreshing && Model.HasRegistration && Startup.IsOn != Model.Startup) await Model.SetStartup(Startup.IsOn);
    }
    private async void OnHandlers(object sender, RoutedEventArgs args)
    {
        if (!_refreshing && Model.HasRegistration && Handlers.IsOn != Model.HandlersRegistered) await Model.SetHandlers(Handlers.IsOn);
    }
    private async void OnInterface(object sender, SelectionChangedEventArgs args)
    {
        if (_refreshing || Interfaces.SelectedItem is not InterfaceChoice choice || choice.InterfaceId == Model.Interface.Input) return;
        Model.Interface.Input = choice.InterfaceId;
        await Model.Commit(Model.Interface);
    }
    private async void OnEncryption(object sender, SelectionChangedEventArgs args)
    {
        if (_refreshing || EncryptionChoice.SelectedItem is not ComboBoxItem { Tag: string encryption } || encryption == Model.Encryption.Input) return;
        Model.Encryption.Input = encryption;
        await Model.Commit(Model.Encryption);
    }
    private void OnLanguage(object sender, SelectionChangedEventArgs args)
    {
        if (!_refreshing && Languages.SelectedItem is ComboBoxItem { Tag: string language }) Model.SelectLanguage(language);
    }
    private async void OnTheme(object sender, SelectionChangedEventArgs args)
    {
        if (!_refreshing && Model.CanSelectTheme && Theme.SelectedItem is ComboBoxItem { Tag: string theme }) await Model.SelectTheme(theme);
    }
    private void OnDestination(object sender, RoutedEventArgs args) => DestinationRequested?.Invoke(this, EventArgs.Empty);
    private void OnProxy(object sender, RoutedEventArgs args) => ProxyRequested?.Invoke(this, EventArgs.Empty);
    private void OnNumberLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is NumberBox { Tag: Preference field } number && TextEditor.Find(number) is { } editor && _editors.Add(editor))
        {
            // NumberBox's focused TextBox does not inherit its HelpText.
            void ForwardHelp() => AutomationProperties.SetHelpText(editor, AutomationProperties.GetHelpText(number));
            number.RegisterPropertyChangedCallback(AutomationProperties.HelpTextProperty, (_, _) => ForwardHelp());
            ForwardHelp();
            Watch(editor);
            editor.TextChanged += (_, _) => { if (number.IsEnabled) field.Input = editor.Text; };
        }
    }

    private void Watch(TextBox editor)
    {
        editor.TextCompositionStarted += (_, _) => _composing.Add(editor);
        editor.TextCompositionEnded += (_, _) => _composing.Remove(editor);
    }

    private async void OnFieldKey(object sender, KeyRoutedEventArgs args)
    {
        if (sender is not Control { Tag: Preference field } control || TextEditor.Find(control) is not { } editor || _composing.Contains(editor)) return;
        if (args.Key == VirtualKey.Escape)
        {
            args.Handled = true;
            field.Cancel();
            editor.Text = field.Input;
        }
        else if (args.Key == VirtualKey.Enter)
        {
            args.Handled = true;
            field.Input = editor.Text;
            await Model.Commit(field);
        }
    }

    private async void OnFieldDeparture(object sender, RoutedEventArgs args)
    {
        if (sender is not Control { Tag: Preference field } control || TextEditor.Find(control) is not { } editor || _composing.Contains(editor)) return;
        var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        if (focused == Browse) return;
        for (var current = focused; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current == control) return;
            if (current == this)
            {
                field.Input = editor.Text;
                await Model.Depart(field);
                return;
            }
        }
    }
}
