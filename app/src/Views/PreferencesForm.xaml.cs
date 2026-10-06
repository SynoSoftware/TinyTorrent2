using System.ComponentModel;
using System.Net.NetworkInformation;
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
    private InterfaceChoice? _unavailableInterface;
    public Preferences Model { get; }
    public event EventHandler? DestinationRequested;

    public PreferencesForm(Preferences model)
    {
        Model = model;
        InitializeComponent();
        _scheduler = new Scheduler(model);
        ScheduleContent.Content = _scheduler;
        _scheduler.SpeedRequested += (_, _) => Navigate(new(PreferenceSection.Transfers, "download_limit"));
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
        RefreshInterfaces();
        _ = Model.ObserveRegistration();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        Model.TextChanged -= OnText;
        Model.PropertyChanged -= OnModel;
    }

    private void OnText(object? sender, EventArgs args) => RefreshText();
    private void OnModel(object? sender, PropertyChangedEventArgs args)
    {
        _refreshing = true;
        Languages.SelectedItem = Model.Language == "es" ? Spanish : English;
        Theme.SelectedItem = Model.Theme switch { "light" => LightTheme, "dark" => DarkTheme, _ => SystemTheme };
        foreach (InterfaceChoice item in Interfaces.Items)
            if (item.InterfaceId == Model.Interface.Input) Interfaces.SelectedItem = item;
        _refreshing = false;
    }

    internal void RefreshText()
    {
        _refreshing = true;
        PageTitle.Text = Model.Text.Get("finding", "settings");
        Label(GeneralCategory, "general");
        Label(TransfersCategory, "transfers");
        Label(NetworkCategory, "network");
        Label(ScheduleCategory, "schedule");
        Label(AppearanceCategory, "appearance");
        AutomationProperties.SetName(Categories, Model.Text.Get("preferences", "categories"));
        Label(DownloadsSection, "adding", "downloads_hint");
        ShowAddRow.Description = Model.Text.Get("preferences", "show_add_hint");
        Label(StartupSection, "startup", "startup_hint");
        SignInRow.Header = Model.Text.Get("preferences", "start_signin");
        AutomationProperties.SetName(Startup, SignInRow.Header);
        TrayRow.Description = Model.Text.Get("preferences", "start_in_tray_hint");
        DefaultsSection.Header = Model.Text.Get("preferences", "defaults");
        Label(PowerSection, "power", "power_hint");
        Label(SpeedSection, "speed", "speed_hint");
        Label(AlternativeSection, "alternative", "alternative_hint");
        Label(QueueSection, "queue", "queue_hint");
        Label(SeedingSection, "seeding", "seeding_hint");
        Label(NetworkSection, "connections", "network_hint");
        PortRow.Description = Model.Text.Get("preferences", "port_hint");
        MappingRow.Description = Model.Text.Get("preferences", "mapping_hint");
        InterfaceRow.Description = Model.Text.Get("preferences", "interface_hint");
        ConnectionsRow.Description = Model.Text.Get("preferences", "connections_hint");
        Label(Browse, "browse", "add");
        Label(StartupSettings, "startup_settings");
        Label(OpenDefaults, "open_defaults");
        Label(Unregister, "remove_handler");
        Label(ScheduleSection, "weekly_schedule", "schedule_hint");
        Label(AppearanceSection, "appearance", "appearance_hint");
        LanguageRow.Header = Model.Text.Get("preferences", "language");
        AutomationProperties.SetName(Languages, LanguageRow.Header);
        Label(English, EnglishLabel, "english");
        Label(Spanish, SpanishLabel, "spanish");
        Languages.SelectedItem = Model.Language == "es" ? Spanish : English;
        ThemeRow.Header = Model.Text.Get("preferences", "theme");
        AutomationProperties.SetName(Theme, ThemeRow.Header);
        Label(SystemTheme, SystemLabel, "system_theme");
        Label(LightTheme, LightLabel, "light_theme");
        Label(DarkTheme, DarkLabel, "dark_theme");
        Theme.SelectedItem = Model.Theme switch { "light" => LightTheme, "dark" => DarkTheme, _ => SystemTheme };
        foreach (InterfaceChoice item in Interfaces.Items)
            if (item.InterfaceId.Length == 0) item.Text = Model.Text.Get("preferences", "any_interface");
        if (_unavailableInterface is { } unavailable)
            unavailable.Text = Model.Text.Format("preferences", "unavailable_interface", unavailable.InterfaceId);
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

    public void RefreshInterfaces()
    {
        _refreshing = true;
        try
        {
            Interfaces.Items.Clear();
            _unavailableInterface = null;
            Interfaces.Items.Add(new InterfaceChoice(string.Empty, Model.Text.Get("preferences", "any_interface")));
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                Interfaces.Items.Add(new InterfaceChoice(adapter.Id, adapter.Name));
            if (Model.Interface.Input.Length > 0 && !Interfaces.Items.Cast<InterfaceChoice>().Any(item => item.InterfaceId == Model.Interface.Input))
            {
                _unavailableInterface = new InterfaceChoice(Model.Interface.Input, Model.Text.Format("preferences", "unavailable_interface", Model.Interface.Input));
                Interfaces.Items.Add(_unavailableInterface);
            }
            Interfaces.SelectedItem = Interfaces.Items.Cast<InterfaceChoice>().First(item => item.InterfaceId == Model.Interface.Input);
        }
        catch (NetworkInformationException error) { Model.Interface.Reject(error); }
        finally { _refreshing = false; }
    }

    // The corner marks overhang the page; the clip keeps them off the navigation
    // pane and the caption.
    private void OnRootSize(object sender, SizeChangedEventArgs args) =>
        Root.Clip = new RectangleGeometry { Rect = new(0, 0, args.NewSize.Width, args.NewSize.Height) };

    private void OnCategory(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is not SelectorBarItem item) return;
        foreach (var panel in new FrameworkElement[] { General, Transfers, Network, Schedule, Appearance })
            panel.Visibility = panel.Name == (string)item.Tag ? Visibility.Visible : Visibility.Collapsed;
        Body.ChangeView(null, 0, null, true);
    }

    internal void Navigate(PreferenceTarget target)
    {
        Categories.SelectedItem = new[] { GeneralCategory, TransfersCategory, NetworkCategory, ScheduleCategory, AppearanceCategory }[(int)target.Section];
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            UpdateLayout();
            if (target.Field == "add_period") { _scheduler.FocusAdd(); return; }
            Control? control = target.Field switch
            {
                "start_signin" => Startup,
                "startup_settings" => StartupSettings,
                "open_defaults" => OpenDefaults,
                "remove_handler" => Unregister,
                "network_interface" => Interfaces,
                "language" => Languages,
                "theme" => Theme,
                null => Categories,
                _ => FindField(this, target.Field)
            };
            if (control is null) return;
            control.StartBringIntoView();
            if (control is NumberBox && TextEditor.Find(control) is { } editor) editor.Focus(FocusState.Programmatic);
            else control.Focus(FocusState.Programmatic);
        });
    }

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
    private async void OnInterface(object sender, SelectionChangedEventArgs args)
    {
        if (_refreshing || Interfaces.SelectedItem is not InterfaceChoice choice || choice.InterfaceId == Model.Interface.Input) return;
        Model.Interface.Input = choice.InterfaceId;
        await Model.Commit(Model.Interface);
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
    private void OnNumberLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is NumberBox { Tag: Preference field } number && TextEditor.Find(number) is { } editor && _editors.Add(editor))
        {
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
                await Model.Commit(field);
                return;
            }
        }
    }
}

public sealed class InterfaceChoice(string interfaceId, string text) : INotifyPropertyChanged
{
    private string _text = text;
    public string InterfaceId { get; } = interfaceId;
    public string Text
    {
        get => _text;
        internal set { _text = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public override string ToString() => Text;
}
