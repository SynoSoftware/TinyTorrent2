using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Syno.TinyTorrent.Shared;

namespace Syno.TinyTorrent.Controls;

/// <summary>
/// A folder setting's editor: the path field and Browse, which asks the host
/// to choose the folder through <see cref="FolderRequested"/>.
/// </summary>
/// <remarks>
/// It is a panel, not a control, so <see cref="SettingsRow"/> leaves the
/// field's enabling and name to the box, and the Settings page reaches the
/// path field as the row's editor. A panel has no automation peer, so the box
/// passes the help text the row gives it to the path field.
/// </remarks>
public sealed partial class FolderBox : Grid
{
    public static readonly DependencyProperty SettingProperty = DependencyProperty.Register(
        nameof(Setting),
        typeof(Setting),
        typeof(FolderBox),
        new PropertyMetadata(null, (box, _) => ((FolderBox)box).Attach())
    );
    public static readonly DependencyProperty BrowseTextProperty = DependencyProperty.Register(
        nameof(BrowseText),
        typeof(string),
        typeof(FolderBox),
        new PropertyMetadata(
            string.Empty,
            (box, args) => ((FolderBox)box)._browse.Text = (string)args.NewValue
        )
    );
    public static readonly DependencyProperty BrowseTipProperty = DependencyProperty.Register(
        nameof(BrowseTip),
        typeof(string),
        typeof(FolderBox),
        new PropertyMetadata(
            string.Empty,
            (box, args) => ToolTipService.SetToolTip(((FolderBox)box)._browse, args.NewValue)
        )
    );

    private readonly TextBox _path = new() { FlowDirection = FlowDirection.LeftToRight };
    private readonly ActionButton _browse = new() { Glyph = Segoe.FolderSearch };

    public Setting? Setting
    {
        get => (Setting?)GetValue(SettingProperty);
        set => SetValue(SettingProperty, value);
    }

    public string BrowseText
    {
        get => (string)GetValue(BrowseTextProperty);
        set => SetValue(BrowseTextProperty, value);
    }

    public string BrowseTip
    {
        get => (string)GetValue(BrowseTipProperty);
        set => SetValue(BrowseTipProperty, value);
    }

    /// <summary>Raised when Browse is clicked, with the setting whose folder to choose.</summary>
    public event EventHandler<Setting>? FolderRequested;

    public FolderBox()
    {
        Width = 360;
        ColumnSpacing = 12;
        ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        SetColumn(_browse, 1);
        Children.Add(_path);
        Children.Add(_browse);
        _browse.Click += OnBrowse;
        RegisterPropertyChangedCallback(
            AutomationProperties.AutomationIdProperty,
            (_, _) => Identify()
        );
        RegisterPropertyChangedCallback(
            AutomationProperties.HelpTextProperty,
            (_, _) =>
                AutomationProperties.SetHelpText(_path, AutomationProperties.GetHelpText(this))
        );
    }

    private void Attach()
    {
        if (Setting is not { } setting)
        {
            return;
        }
        _path.SetBinding(
            TextBox.TextProperty,
            new Binding
            {
                Source = setting,
                Path = new PropertyPath(nameof(Setting.Input)),
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            }
        );
        _path.SetBinding(Control.IsEnabledProperty, Bind(setting, nameof(Setting.CanEdit)));
        _browse.SetBinding(Control.IsEnabledProperty, Bind(setting, nameof(Setting.CanEdit)));
        _path.SetBinding(AutomationProperties.NameProperty, Bind(setting, nameof(Setting.Label)));
    }

    private static Binding Bind(Setting setting, string path) =>
        new() { Source = setting, Path = new PropertyPath(path) };

    private void Identify()
    {
        var id = AutomationProperties.GetAutomationId(this);
        AutomationProperties.SetAutomationId(_path, id + "Path");
        AutomationProperties.SetAutomationId(_browse, id + "Browse");
    }

    private void OnBrowse(object sender, RoutedEventArgs args)
    {
        if (Setting is { } setting)
        {
            FolderRequested?.Invoke(this, setting);
        }
    }
}
