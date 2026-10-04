using System.ComponentModel;
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
        Theme.Content = Model.ThemeText;
        AutomationProperties.SetName(Theme, Model.ThemeText);
        foreach (ComboBoxItem item in Interfaces.Items)
            if (Equals(item.Tag, Model.Interface.Input)) Interfaces.SelectedItem = item;
        _refreshing = false;
        if (_editor != Model.Draft)
        {
            _editor = Model.Draft;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (Model.IsEditing) StartTime.Focus(FocusState.Programmatic);
                else AddPeriod.Focus(FocusState.Programmatic);
            });
        }
    }
    private void OnWeek(object? sender, EventArgs args) => RefreshWeek();

    internal void RefreshText()
    {
        _refreshing = true;
        Label(GeneralCategory, "general");
        Label(TransfersCategory, "transfers");
        Label(NetworkCategory, "network");
        Label(ScheduleCategory, "schedule");
        Label(AppearanceCategory, "appearance");
        AutomationProperties.SetName(Categories, Model.Text.Get("preferences", "categories"));
        AddingTitle.Text = Model.Text.Get("preferences", "adding");
        CompletionTitle.Text = Model.Text.Get("preferences", "completion");
        PowerTitle.Text = Model.Text.Get("preferences", "power");
        StartupTitle.Text = Model.Text.Get("preferences", "startup");
        DefaultsTitle.Text = Model.Text.Get("preferences", "defaults");
        SpeedTitle.Text = Model.Text.Get("preferences", "speed");
        SpeedHint.Text = Model.Text.Get("preferences", "speed_hint");
        AlternativeTitle.Text = Model.Text.Get("preferences", "alternative");
        QueueTitle.Text = Model.Text.Get("preferences", "queue");
        SeedingTitle.Text = Model.Text.Get("preferences", "seeding");
        SeedingHint.Text = Model.Text.Get("preferences", "seeding_hint");
        NetworkTitle.Text = Model.Text.Get("preferences", "network");
        ConnectionsTitle.Text = Model.Text.Get("preferences", "connections");
        Startup.Header = Model.Text.Get("preferences", "start_signin");
        AutomationProperties.SetName(Startup, (string)Startup.Header);
        Label(Browse, "browse", "add");
        Label(StartupSettings, "startup_settings");
        Label(OpenDefaults, "open_defaults");
        Label(Unregister, "remove_handler");
        Label(AddPeriod, "add_period");
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
        ScheduleHint.Text = Model.Text.Get("preferences", "schedule_hint");
        NormalLegend.Text = Model.Text.Get("preferences", "normal");
        AlternativeLegend.Text = Model.Text.Get("preferences", "alternative");
        PausedLegend.Text = Model.Text.Get("preferences", "paused");
        Languages.Header = Model.Text.Get("preferences", "language");
        AutomationProperties.SetName(Languages, (string)Languages.Header);
        English.Content = Model.Text.Get("preferences", "english");
        Spanish.Content = Model.Text.Get("preferences", "spanish");
        Languages.SelectedItem = Model.Language == "es" ? Spanish : English;
        Theme.Content = Model.ThemeText;
        AutomationProperties.SetName(Theme, Model.ThemeText);
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
        Week.Children.Clear();
        for (var day = 0; day < 7; day++)
        {
            var row = new Grid { ColumnSpacing = 8, RowSpacing = 4 };
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var label = new TextBlock { Text = Model.Day(day), VerticalAlignment = VerticalAlignment.Center, MinWidth = 90 };
            row.Children.Add(label);
            var timeline = new Grid { Height = 24 };
            var ranges = Model.Ranges(day).ToArray();
            var descriptions = ranges.Select(Model.Describe).ToArray();
            var summary = string.Join("; ", descriptions);
            for (var index = 0; index < ranges.Length; index++)
            {
                var range = ranges[index];
                timeline.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(range.End - range.Start, GridUnitType.Star) });
                var template = (DataTemplate)Resources[range.Mode switch
                {
                    ScheduleMode.Paused => "PreferencesPausedTemplate",
                    ScheduleMode.Alternative => "PreferencesAlternativeTemplate",
                    _ => "PreferencesNormalTemplate"
                }];
                var bar = (Border)template.LoadContent();
                Grid.SetColumn(bar, index);
                timeline.Children.Add(bar);
            }
            AutomationProperties.SetName(label, Model.Text.Format("preferences", "day_schedule", Model.Day(day), summary));
            Grid.SetColumn(timeline, 1);
            row.Children.Add(timeline);
            var details = (TextBlock)((DataTemplate)Resources["PreferencesDayFactsTemplate"]).LoadContent();
            details.Text = summary;
            Grid.SetRow(details, 1);
            Grid.SetColumn(details, 1);
            row.Children.Add(details);
            Week.Children.Add(row);
        }
    }

    private void OnCategory(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
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
    private void OnTheme(object sender, RoutedEventArgs args) => Model.SwitchTheme.Execute(null);
    private void OnDestination(object sender, RoutedEventArgs args) => DestinationRequested?.Invoke(this, EventArgs.Empty);
    public static int PausedIndex(bool paused) => paused ? 1 : 0;
    private void OnPeriodMode(object sender, SelectionChangedEventArgs args)
    {
        if (!_refreshing && Model.Draft is { } draft) draft.IsPaused = PeriodMode.SelectedIndex == 1;
    }

    private void OnNumberLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is NumberBox { Tag: Preference field } number && Editor(number) is { } editor && _editors.Add(editor))
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

    private static TextBox? Editor(DependencyObject element)
    {
        if (element is TextBox editor) return editor;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            if (Editor(VisualTreeHelper.GetChild(element, index)) is { } child) return child;
        return null;
    }

    private async void OnFieldKey(object sender, KeyRoutedEventArgs args)
    {
        if (sender is not Control { Tag: Preference field } control || Editor(control) is not { } editor || _composing.Contains(editor)) return;
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
        if (sender is not Control { Tag: Preference field } control || Editor(control) is not { } editor || _composing.Contains(editor)) return;
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
