using System.ComponentModel;
using System.Globalization;
using System.Net.NetworkInformation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Syno.TinyTorrent;

public sealed partial class PreferencesForm : UserControl
{
    private readonly HashSet<TextBox> _composing = [];
    private readonly HashSet<TextBox> _editors = [];
    private bool _refreshing;
    private PeriodDraft? _editor;
    private ComboBoxItem? _unavailableInterface;
    public Preferences Model { get; }
    public event EventHandler? DestinationRequested;

    public PreferencesForm(Preferences model)
    {
        Model = model;
        InitializeComponent();
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
        Model.WeekChanged += OnWeek;
        RefreshText();
        RefreshInterfaces();
        RefreshWeek();
        _ = Model.ObserveRegistration();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        Model.TextChanged -= OnText;
        Model.PropertyChanged -= OnModel;
        Model.WeekChanged -= OnWeek;
    }

    private void OnText(object? sender, EventArgs args) => RefreshText();
    private void OnModel(object? sender, PropertyChangedEventArgs args)
    {
        _refreshing = true;
        Languages.SelectedItem = Model.Language == "es" ? Spanish : English;
        Theme.SelectedItem = Model.Theme switch { "light" => LightTheme, "dark" => DarkTheme, _ => SystemTheme };
        foreach (ComboBoxItem item in Interfaces.Items)
            if (Equals(item.Tag, Model.Interface.Input)) Interfaces.SelectedItem = item;
        _refreshing = false;
        RefreshWeekAppearance();
        if (_editor != Model.Draft)
        {
            _editor = Model.Draft;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (Model.IsEditing)
                {
                    PeriodTitle.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = false });
                    StartTime.Focus(FocusState.Programmatic);
                }
                else AddPeriod.Focus(FocusState.Programmatic);
            });
        }
    }
    private void OnWeek(object? sender, EventArgs args) => RefreshWeek();

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
        Label(AddPeriod, "add_period");
        Label(SpeedLimits, "speed");
        Label(SavePeriod, "save_period");
        Label(CancelPeriod, "cancel", "add");
        PeriodTitle.Text = Model.Text.Get("preferences", "period_title");
        DaysTitle.Text = Model.Text.Get("preferences", "start_days");
        StartTime.Header = Model.Text.Get("preferences", "start_time");
        EndTime.Header = Model.Text.Get("preferences", "end_time");
        AutomationProperties.SetName(StartTime, (string)StartTime.Header);
        AutomationProperties.SetName(EndTime, (string)EndTime.Header);
        PeriodMode.Header = Model.Text.Get("preferences", "period_mode");
        AutomationProperties.SetName(PeriodMode, (string)PeriodMode.Header);
        Label(AlternativeMode, "alternative");
        Label(PausedMode, "paused");
        Label(ScheduleSection, "weekly_schedule", "schedule_hint");
        PeriodsTitle.Text = Model.Text.Get("preferences", "periods");
        EmptyPeriods.Text = Model.Text.Get("preferences", "empty_periods");
        NormalLegend.Text = Model.Text.Get("preferences", "normal");
        AlternativeLegend.Text = Model.Text.Get("preferences", "alternative");
        PausedLegend.Text = Model.Text.Get("preferences", "paused");
        Label(AppearanceSection, "appearance", "appearance_hint");
        LanguageRow.Header = Model.Text.Get("preferences", "language");
        AutomationProperties.SetName(Languages, LanguageRow.Header);
        English.Content = Model.Text.Get("preferences", "english");
        Spanish.Content = Model.Text.Get("preferences", "spanish");
        Languages.SelectedItem = Model.Language == "es" ? Spanish : English;
        ThemeRow.Header = Model.Text.Get("preferences", "theme");
        AutomationProperties.SetName(Theme, ThemeRow.Header);
        Label(SystemTheme, "system_theme");
        Label(LightTheme, "light_theme");
        Label(DarkTheme, "dark_theme");
        Theme.SelectedItem = Model.Theme switch { "light" => LightTheme, "dark" => DarkTheme, _ => SystemTheme };
        foreach (ComboBoxItem item in Interfaces.Items)
            if (Equals(item.Tag, string.Empty)) item.Content = Model.Text.Get("preferences", "any_interface");
        if (_unavailableInterface is { } unavailable)
            unavailable.Content = Model.Text.Format("preferences", "unavailable_interface", unavailable.Tag);
        _refreshing = false;
    }

    private void Label(ContentControl control, string key, string group = "preferences")
    {
        control.Content = Model.Text.Get(group, key);
        AutomationProperties.SetName(control, (string)control.Content);
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
            Interfaces.Items.Add(new ComboBoxItem { Content = Model.Text.Get("preferences", "any_interface"), Tag = string.Empty });
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                Interfaces.Items.Add(new ComboBoxItem { Content = adapter.Name, Tag = adapter.Id });
            if (Model.Interface.Input.Length > 0 && !Interfaces.Items.Cast<ComboBoxItem>().Any(item => Equals(item.Tag, Model.Interface.Input)))
            {
                _unavailableInterface = new ComboBoxItem { Content = Model.Text.Format("preferences", "unavailable_interface", Model.Interface.Input), Tag = Model.Interface.Input };
                Interfaces.Items.Add(_unavailableInterface);
            }
            Interfaces.SelectedItem = Interfaces.Items.Cast<ComboBoxItem>().First(item => Equals(item.Tag, Model.Interface.Input));
        }
        catch (NetworkInformationException error) { Model.Interface.Reject(error); }
        finally { _refreshing = false; }
    }

    private void RefreshWeek()
    {
        for (var day = 0; day < 7; day++)
        {
            if (Week.Children.Count <= day)
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add((TextBlock)((DataTemplate)Resources["PreferencesDayTemplate"]).LoadContent());
                var track = new Grid();
                Grid.SetColumn(track, 1);
                row.Children.Add(track);
                Week.Children.Add(row);
            }
            var current = (Grid)Week.Children[day];
            var label = (TextBlock)current.Children[0];
            label.Text = Model.ShortDay(day);
            var timeline = (Grid)current.Children[1];
            var ranges = Model.Ranges(day).ToArray();
            if (!timeline.Children.Cast<FrameworkElement>().Select(element => element.Tag).SequenceEqual(ranges))
            {
                timeline.Children.Clear();
                timeline.ColumnDefinitions.Clear();
                foreach (var range in ranges)
                {
                    timeline.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(range.End - range.Start, GridUnitType.Star) });
                    var template = (DataTemplate)Resources[range.Period is null ? "PreferencesNormalTemplate" : "PreferencesPeriodTemplate"];
                    var segment = (FrameworkElement)template.LoadContent();
                    segment.Tag = range;
                    if (segment is Button button) button.Command = range.Period?.Edit;
                    Grid.SetColumn(segment, timeline.Children.Count);
                    timeline.Children.Add(segment);
                }
            }
            for (var index = 0; index < ranges.Length; index++)
            {
                var range = ranges[index];
                var segment = (FrameworkElement)timeline.Children[index];
                var caption = segment is Button button ? (TextBlock)button.Content : (TextBlock)((Border)segment).Child;
                caption.Text = Model.FormatMode(range.Mode);
                var description = Model.Text.Format("preferences", "day_schedule", Model.Day(day), Model.Describe(range));
                AutomationProperties.SetName(segment is Button ? segment : caption, description);
                ToolTipService.SetToolTip(segment, description);
            }
            var summary = string.Join("; ", ranges.Select(Model.Describe));
            AutomationProperties.SetName(label, Model.Text.Format("preferences", "day_schedule", Model.Day(day), summary));
        }
        for (var index = 0; index < HourRuler.Children.Count; index++)
            ((TextBlock)HourRuler.Children[index]).Text = (index * 3).ToString("00", CultureInfo.CurrentCulture);
        RefreshWeekAppearance();
    }

    private void RefreshWeekAppearance()
    {
        foreach (Grid row in Week.Children)
            foreach (var button in ((Grid)row.Children[1]).Children.OfType<Button>())
                button.Style = (Style)Resources[!Model.Schedule.IsOn ? "PreferencesInactiveStyle" :
                    ((ScheduleRange)button.Tag).Mode == ScheduleMode.Paused ? "PreferencesPausedStyle" : "PreferencesAlternativeStyle"];
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
        DispatcherQueue.TryEnqueue(() =>
        {
            Control? control = target.Field switch
            {
                "start_signin" => Startup,
                "startup_settings" => StartupSettings,
                "open_defaults" => OpenDefaults,
                "remove_handler" => Unregister,
                "network_interface" => Interfaces,
                "language" => Languages,
                "theme" => Theme,
                "add_period" => AddPeriod,
                null => Categories,
                _ => FindField(this, target.Field)
            };
            if (control is null) return;
            control.StartBringIntoView();
            control.Focus(FocusState.Programmatic);
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
        if (_refreshing || Interfaces.SelectedItem is not ComboBoxItem { Tag: string value } || value == Model.Interface.Input) return;
        Model.Interface.Input = value;
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
    private void OnSpeedLimits(object sender, RoutedEventArgs args) => Navigate(new(PreferenceSection.Transfers, "download_limit"));
    public static Visibility Empty(bool hasPeriods) => hasPeriods ? Visibility.Collapsed : Visibility.Visible;
    public static int PausedIndex(bool paused) => paused ? 1 : 0;
    private void OnPeriodMode(object sender, SelectionChangedEventArgs args)
    {
        if (!_refreshing && Model.Draft is { } draft) draft.IsPaused = PeriodMode.SelectedIndex == 1;
    }

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
        if (focused == Browse || focused == CancelPeriod) return;
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
