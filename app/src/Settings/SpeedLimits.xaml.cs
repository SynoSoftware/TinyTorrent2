using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Shell;

namespace Syno.TinyTorrent.Settings;

public sealed partial class SpeedLimits : UserControl
{
    private readonly Windows.UI.ViewManagement.UISettings _display = new();
    private readonly SettingsRow[] _rows;
    public MainViewModel Main { get; }
    public Editor Model => Main.Settings;
    internal LimitSelector Selector { get; }
    internal event EventHandler? ConnectionRequested;

    internal SpeedLimits(MainViewModel main, bool inline = false)
    {
        Main = main;
        InitializeComponent();
        Selector = new(main);
        ModeRow.Content = Selector;
        _rows =
        [
            AddRate(StandardRows, Model.Download, "DownloadLimit"),
            AddRate(StandardRows, Model.Upload, "UploadLimit"),
            AddRate(AlternativeRows, Model.AlternativeDownload, "AlternativeDownload"),
            AddRate(AlternativeRows, Model.AlternativeUpload, "AlternativeUpload"),
        ];
        if (inline)
        {
            ModeCard.Visibility = Visibility.Collapsed;
            Profiles.Margin = new Thickness(0);
            foreach (var card in new[] { StandardCard, AlternativeCard })
            {
                card.BorderThickness = new Thickness(0);
                card.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }
            _rows[1].BorderThickness = new Thickness(0);
            _rows[3].BorderThickness = new Thickness(0);
            Connection.BorderThickness = new Thickness(0);
        }
        Loaded += (_, _) =>
        {
            _display.TextScaleFactorChanged += OnTextScale;
            UpdateProfiles();
            Model.TextChanged += OnText;
            OnText(this, EventArgs.Empty);
        };
        Unloaded += (_, _) =>
        {
            _display.TextScaleFactorChanged -= OnTextScale;
            Model.TextChanged -= OnText;
        };
    }

    private SettingsRow AddRate(StackPanel panel, Setting setting, string automationId)
    {
        var editor = new TextBox
        {
            Width = 100,
            InputScope = new InputScope
            {
                Names = { new InputScopeName(InputScopeNameValue.Number) },
            },
        };
        editor.SetBinding(
            TextBox.TextProperty,
            new Binding
            {
                Source = setting,
                Path = new PropertyPath(nameof(Setting.Input)),
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            }
        );
        var row = new SettingsRow { Content = editor, Setting = setting };
        panel.Children.Add(row);
        AutomationProperties.SetAutomationId(editor, automationId);
        RefreshRate(row);
        return row;
    }

    private void OnText(object? sender, EventArgs args)
    {
        Bindings.Update();
        foreach (var row in _rows)
            RefreshRate(row);
    }

    private void RefreshRate(SettingsRow row)
    {
        var editor = (TextBox)row.Content;
        var setting = row.Setting!;
        row.Unit = setting.RateUnit;
        row.Header =
            setting == Model.Download || setting == Model.AlternativeDownload
                ? Model.Text.Get("settings", "download_speed")
                : Model.Text.Get("settings", "upload_speed");
        editor.PlaceholderText = Model.Text.Get("transfer_limits", "unlimited");
        AutomationProperties.SetName(editor, row.Header);
    }

    internal Control? Field(string name) =>
        name switch
        {
            "limit_mode" => Selector,
            "connection_setup" => Connection,
            _ => _rows.FirstOrDefault(row => row.Setting!.Name == name)?.Content as Control,
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
        Profiles.ColumnDefinitions[1].Width = stacked
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(AlternativeProfile, stacked ? 0 : 1);
        Grid.SetRow(AlternativeProfile, stacked ? 1 : 0);
    }

    private void OnConnection(object sender, RoutedEventArgs args) =>
        ConnectionRequested?.Invoke(this, EventArgs.Empty);
}
