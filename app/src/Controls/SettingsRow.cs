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
    public static readonly DependencyProperty IconProperty = Element(nameof(Icon));
    public static readonly DependencyProperty UnitProperty = Text(nameof(Unit));
    public static readonly DependencyProperty UnitSelectorProperty = DependencyProperty.Register(
        nameof(UnitSelector),
        typeof(ComboBox),
        typeof(SettingsRow),
        new PropertyMetadata(null, (row, _) => ((SettingsRow)row).Update())
    );
    public bool IsAdvanced { get; set; }
    private double _unitWidth;

    internal void Align(double unitWidth)
    {
        if (_unitWidth == unitWidth)
            return;
        _unitWidth = unitWidth;
        Update();
    }

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
    public UIElement? Icon
    {
        get => (UIElement?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
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
        AutomationProperties.SetName(this, Header);
        var hasPrimary = Header.Length > 0 || Description.Length > 0 || Error.Length > 0
            || Caution.Length > 0 || Content is not null || StateText.Length > 0
            || Unit.Length > 0 || UnitSelector is not null;
        if (GetTemplateChild("RowRoot") is Grid root)
        {
            root.MinHeight = Description.Length > 0 ? 64 : 52;
            root.RowDefinitions[0].Height = hasPrimary ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            root.RowDefinitions[0].MinHeight = hasPrimary ? 32 : 0;
        }
        if (GetTemplateChild("DetailPresenter") is ContentPresenter detail)
            detail.Margin = new Thickness(0, hasPrimary ? 8 : 0, 0, 0);
        Show("DescriptionLabel", Description.Length > 0);
        Show("IconPresenter", Icon is not null);
        Show("StateLabel", StateText.Length > 0);
        Show("UnitLabel", Unit.Length > 0);
        Show("UnitSelectorHost", UnitSelector is not null);
        if (GetTemplateChild("UnitLabel") is TextBlock unit)
            unit.MinWidth = _unitWidth;
        var help = string.Join(
            " ",
            new[] { Error, Description, Caution, Unit }.Where(text => text.Length > 0)
        );
        var tip = string.Join(" ", new[] { Error, Caution }.Where(text => text.Length > 0));
        // A disabled control shows no tooltip and passes the pointer to its
        // parent, so the presenter around it shows the tooltip instead.
        var disabled = Content is Control { IsEnabled: false };
        if (GetTemplateChild("ContentHost") is UIElement host)
            ToolTipService.SetToolTip(host, disabled && tip.Length > 0 ? tip : null);
        foreach (var element in new[] { Content, Detail, UnitSelector }.OfType<UIElement>())
        {
            AutomationProperties.SetHelpText(element, help);
            ToolTipService.SetToolTip(
                element,
                tip.Length == 0 || disabled && ReferenceEquals(element, Content) ? null : tip
            );
        }
    }

    private void Show(string part, bool visible)
    {
        if (GetTemplateChild(part) is UIElement element)
            element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }
}
