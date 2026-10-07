using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace Syno.TinyTorrent.Controls;

// PreferencesForm.xaml supplies the shared template for each setting.
public sealed partial class SettingsRow : ContentControl
{
    public static readonly DependencyProperty HeaderProperty = Text(nameof(Header));
    public static readonly DependencyProperty DescriptionProperty = Text(nameof(Description));
    public static readonly DependencyProperty ErrorProperty = Text(nameof(Error));
    public static readonly DependencyProperty CautionProperty = Text(nameof(Caution));
    public static readonly DependencyProperty StateTextProperty = Text(nameof(StateText));
    public static readonly DependencyProperty DetailProperty = Element(nameof(Detail));

    public string Header { get => (string)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public string Error { get => (string)GetValue(ErrorProperty); set => SetValue(ErrorProperty, value); }
    public string Caution { get => (string)GetValue(CautionProperty); set => SetValue(CautionProperty, value); }
    public string StateText { get => (string)GetValue(StateTextProperty); set => SetValue(StateTextProperty, value); }
    // A control as wide as the row, below the header and the setting's control.
    public object? Detail { get => GetValue(DetailProperty); set => SetValue(DetailProperty, value); }

    public static string State(bool isOn, string on, string off) => isOn ? on : off;

    private static DependencyProperty Text(string name) => DependencyProperty.Register(name, typeof(string),
        typeof(SettingsRow), new PropertyMetadata(string.Empty, (row, _) => ((SettingsRow)row).Update()));

    private static DependencyProperty Element(string name) => DependencyProperty.Register(name, typeof(object),
        typeof(SettingsRow), new PropertyMetadata(null, (row, _) => ((SettingsRow)row).Update()));

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        Update();
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        if (oldContent is Control old) old.IsEnabledChanged -= OnContentEnabled;
        if (newContent is Control control) control.IsEnabledChanged += OnContentEnabled;
        Update();
    }

    private void OnContentEnabled(object sender, DependencyPropertyChangedEventArgs args) => Update();

    private void Update()
    {
        if (GetTemplateChild("HeaderRun") is Run label) label.Text = Header;
        if (GetTemplateChild("ErrorText") is Run error)
            error.Text = Error.Length > 0 ? (Header.Length > 0 ? " · " : string.Empty) + Error : string.Empty;
        if (GetTemplateChild("CautionText") is Run caution)
            caution.Text = Caution.Length > 0 ? (Header.Length > 0 || Error.Length > 0 ? " · " : string.Empty) + Caution : string.Empty;
        if (GetTemplateChild("HeaderText") is TextBlock header)
            ToolTipService.SetToolTip(header, string.Join(" · ", new[] { Header, Error, Caution }.Where(text => text.Length > 0)));
        Show("DetailPresenter", Detail is not null);
        Show("StateLabel", StateText.Length > 0);
        var help = string.Join(" ", new[] { Error, Description, Caution }.Where(text => text.Length > 0));
        // A disabled control shows no tooltip and passes the pointer to its
        // parent, so the presenter around it shows the tooltip instead.
        var disabled = Content is Control { IsEnabled: false };
        if (GetTemplateChild("ContentHost") is UIElement host) ToolTipService.SetToolTip(host, disabled ? help : null);
        foreach (var element in new[] { Content, Detail }.OfType<UIElement>())
        {
            AutomationProperties.SetHelpText(element, help);
            ToolTipService.SetToolTip(element, disabled && ReferenceEquals(element, Content) ? null : help);
        }
    }

    private void Show(string part, bool visible)
    {
        if (GetTemplateChild(part) is UIElement element) element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }
}
