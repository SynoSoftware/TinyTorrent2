using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Syno.TinyTorrent.Controls;

// App.xaml supplies the shared template for each setting.
public sealed partial class SettingsRow : ContentControl
{
    public static readonly DependencyProperty HeaderProperty = Text(nameof(Header));
    public static readonly DependencyProperty DescriptionProperty = Text(nameof(Description));
    public static readonly DependencyProperty ErrorProperty = Text(nameof(Error));
    public static readonly DependencyProperty CautionProperty = Text(nameof(Caution));
    public static readonly DependencyProperty StateTextProperty = Text(nameof(StateText));
    public static readonly DependencyProperty DetailProperty = Element(nameof(Detail));
    public static readonly DependencyProperty UnitProperty = Text(nameof(Unit));
    public static readonly DependencyProperty UnitSelectorProperty = DependencyProperty.Register(
        nameof(UnitSelector),
        typeof(ComboBox),
        typeof(SettingsRow),
        new PropertyMetadata(null, (row, _) => ((SettingsRow)row).Update())
    );
    public bool IsAdvanced { get; set; }
    private double _unitWidth;

    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public ComboBox? UnitSelector
    {
        get => (ComboBox?)GetValue(UnitSelectorProperty);
        set => SetValue(UnitSelectorProperty, value);
    }

    internal void Align(double unitWidth)
    {
        _unitWidth = unitWidth;
        Update();
    }

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }
    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }
    public string Error
    {
        get => (string)GetValue(ErrorProperty);
        set => SetValue(ErrorProperty, value);
    }
    public string Caution
    {
        get => (string)GetValue(CautionProperty);
        set => SetValue(CautionProperty, value);
    }
    public string StateText
    {
        get => (string)GetValue(StateTextProperty);
        set => SetValue(StateTextProperty, value);
    }

    // A control as wide as the row, below the header and the setting's control.
    public object? Detail
    {
        get => GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    public static string State(bool isOn, string on, string off) => isOn ? on : off;

    private static DependencyProperty Text(string name) =>
        DependencyProperty.Register(
            name,
            typeof(string),
            typeof(SettingsRow),
            new PropertyMetadata(string.Empty, (row, _) => ((SettingsRow)row).Update())
        );

    private static DependencyProperty Element(string name) =>
        DependencyProperty.Register(
            name,
            typeof(object),
            typeof(SettingsRow),
            new PropertyMetadata(null, (row, _) => ((SettingsRow)row).Update())
        );

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        Update();
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        if (oldContent is Control old)
            old.IsEnabledChanged -= OnContentEnabled;
        if (newContent is Control control)
            control.IsEnabledChanged += OnContentEnabled;
        Update();
    }

    private void OnContentEnabled(object sender, DependencyPropertyChangedEventArgs args) =>
        Update();

    private void Update()
    {
        Show("DetailPresenter", Detail is not null);
        Show("StateLabel", StateText.Length > 0);
        Show("UnitLabel", Unit.Length > 0);
        Show("UnitSelectorHost", UnitSelector is not null);
        if (GetTemplateChild("ContentLayout") is Grid layout)
        {
            var aligned = _unitWidth > 0 && (Content is NumberBox or ToggleSwitch
                || Content is TextBox { InputScope: { } scope }
                    && scope.Names.Any(name => name.NameValue == Microsoft.UI.Xaml.Input.InputScopeNameValue.Number)
                || Unit.Length > 0 || UnitSelector is not null);
            layout.ColumnSpacing = 0;
            layout.ColumnDefinitions[0].Width = GridLength.Auto;
            layout.ColumnDefinitions[1].Width = aligned ? new GridLength(160) : GridLength.Auto;
            layout.ColumnDefinitions[2].Width = aligned ? new GridLength(_unitWidth + 12) : GridLength.Auto;
            if (GetTemplateChild("StateLabel") is TextBlock state)
            {
                Grid.SetColumn(state, aligned ? 2 : 0);
                state.Margin = aligned ? new Thickness(12, 0, 0, 0) : new Thickness(0, 0, 12, 0);
            }
            if (GetTemplateChild("UnitLabel") is TextBlock unit)
                unit.Margin = new Thickness(12, 0, 0, 0);
        }
        var help = string.Join(
            " ",
            new[] { Error, Description, Caution, Unit }.Where(text => text.Length > 0)
        );
        // A disabled control shows no tooltip and passes the pointer to its
        // parent, so the presenter around it shows the tooltip instead.
        var disabled = Content is Control { IsEnabled: false };
        if (GetTemplateChild("ContentHost") is UIElement host)
            ToolTipService.SetToolTip(host, disabled ? help : null);
        foreach (var element in new[] { Content, Detail, UnitSelector }.OfType<UIElement>())
        {
            AutomationProperties.SetHelpText(element, help);
            ToolTipService.SetToolTip(
                element,
                disabled && ReferenceEquals(element, Content) ? null : help
            );
        }
    }

    private void Show(string part, bool visible)
    {
        if (GetTemplateChild(part) is UIElement element)
            element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }
}
