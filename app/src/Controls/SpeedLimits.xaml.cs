using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Syno.TinyTorrent.Views;

namespace Syno.TinyTorrent.Controls;

public sealed partial class SpeedLimits : UserControl
{
    private readonly Windows.UI.ViewManagement.UISettings _display = new();
    private readonly Dictionary<TextBox, SettingsRow> _rows = [];
    public MainViewModel Main { get; }
    public Settings Model => Main.Settings;
    internal LimitSelector Selector { get; }
    internal IReadOnlyList<TextBox> Editors { get; }
    internal event EventHandler? ConnectionRequested;

    internal SpeedLimits(MainViewModel main, Action<TextBox> register, bool inline = false)
    {
        Main = main;
        InitializeComponent();
        Selector = new(main);
        ModeRow.Content = Selector;
        Editors = [
            AddRate(StandardRows, Model.Download, "DownloadLimit"),
            AddRate(StandardRows, Model.Upload, "UploadLimit"),
            AddRate(AlternativeRows, Model.AlternativeDownload, "AlternativeDownload"),
            AddRate(AlternativeRows, Model.AlternativeUpload, "AlternativeUpload"),
        ];
        foreach (var editor in Editors)
            register(editor);
        if (inline)
        {
            ModeCard.Visibility = Visibility.Collapsed;
            Profiles.Margin = new Thickness(0);
            foreach (var card in new[] { StandardCard, AlternativeCard })
            {
                card.BorderThickness = new Thickness(0);
                card.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }
            _rows[Editors[1]].BorderThickness = new Thickness(0);
            _rows[Editors[3]].BorderThickness = new Thickness(0);
            Connection.BorderThickness = new Thickness(0);
        }
        Loaded += (_, _) =>
        {
            _display.TextScaleFactorChanged += OnTextScale;
            UpdateProfiles();
            Model.PropertyChanged += OnModel;
            Main.PropertyChanged += OnModel;
            Bindings.Update();
        };
        Unloaded += (_, _) =>
        {
            _display.TextScaleFactorChanged -= OnTextScale;
            Model.PropertyChanged -= OnModel;
            Main.PropertyChanged -= OnModel;
        };
    }

    private TextBox AddRate(StackPanel panel, Setting setting, string automationId)
    {
        var editor = new TextBox
        {
            Width = 100,
            Tag = setting,
            InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.Number) } },
        };
        editor.SetBinding(TextBox.TextProperty, new Binding
        {
            Source = setting, Path = new PropertyPath(nameof(Setting.Input)),
            Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        });
        editor.SetBinding(IsEnabledProperty, new Binding
        {
            Source = setting, Path = new PropertyPath(nameof(Setting.CanEdit)),
        });
        var row = new SettingsRow { Content = editor, Unit = setting.RateUnit };
        _rows.Add(editor, row);
        row.SetBinding(SettingsRow.ErrorProperty, new Binding
        {
            Source = setting, Path = new PropertyPath(nameof(Setting.Message)),
        });
        panel.Children.Add(row);
        AutomationProperties.SetAutomationId(editor, automationId);
        RefreshRate(editor);
        return editor;
    }

    private void OnModel(object? sender, PropertyChangedEventArgs args)
    {
        Bindings.Update();
        foreach (var editor in Editors)
            RefreshRate(editor);
    }

    private void RefreshRate(TextBox editor)
    {
        var setting = (Setting)editor.Tag;
        var row = _rows[editor];
        row.Header = setting == Model.Download || setting == Model.AlternativeDownload
            ? Model.Text.Get("settings", "download_speed") : Model.Text.Get("settings", "upload_speed");
        editor.PlaceholderText = Model.Text.Get("transfer_limits", "unlimited");
        AutomationProperties.SetName(editor, row.Header);
    }

    internal Control? Field(string name) => name switch
    {
        "limit_mode" => Selector,
        "connection_setup" => Connection,
        _ => Editors.FirstOrDefault(editor => ((Setting)editor.Tag).Name == name),
    };

    internal void FocusConnection() => Connection.Focus(FocusState.Programmatic);

    private void OnTextScale(Windows.UI.ViewManagement.UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(UpdateProfiles);

    private void OnProfilesSize(object sender, SizeChangedEventArgs args) => UpdateProfiles();

    private void UpdateProfiles()
    {
        var stacked = Profiles.ActualWidth < 640 * _display.TextScaleFactor;
        Profiles.ColumnSpacing = stacked ? 0 : 8;
        Profiles.RowSpacing = stacked ? 8 : 0;
        Profiles.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(AlternativeProfile, stacked ? 0 : 1);
        Grid.SetRow(AlternativeProfile, stacked ? 1 : 0);
    }

    private void OnConnection(object sender, RoutedEventArgs args) =>
        ConnectionRequested?.Invoke(this, EventArgs.Empty);
}
